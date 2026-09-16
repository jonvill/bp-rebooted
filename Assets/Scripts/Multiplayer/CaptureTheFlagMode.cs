using System.Collections.Generic;
using UnityEngine;

namespace BPRE.Multiplayer
{
	/// <summary>
	/// A flag is placed somewhere in the level by the host. Touch it to pick it up, bring it
	/// back to the start zone to score. Stopping your contraption drops the flag where you are.
	/// The host owns the flag state; players send pickup, capture and drop requests.
	/// </summary>
	public sealed class CaptureTheFlagMode : RoundBasedMode
	{
		private const byte MsgFlagState = 10;

		private const byte MsgPickupRequest = 11;

		private const byte MsgCaptureRequest = 12;

		private const byte MsgDropRequest = 13;

		private const float PickupRadius = 1.6f;

		private const float BaseRadius = 5f;

		private const float FlagHeight = 1.4f;

		private const float PickupCooldown = 2.5f;

		private const float FlagMinDistance = 60f;

		private const float FlagMaxDistance = 400f;

		private const float FlagMaxRise = 40f;

		private const float FlagMaxDrop = 80f;

		private float m_pickupBlockedUntil;

		private float m_hostPickupBlockedUntil;

		private int m_carrierId;

		private Vector3 m_flagPosition;

		private bool m_flagActive;

		private GameObject m_flagObject;

		private GameObject m_baseObject;

		private Vector3 m_basePosition;

		private bool m_haveBase;

		private bool m_baseSearched;

		private Vector3 m_lastLocalPigPosition;

		private string m_capturesField;

		public int CapturesToWin { get; set; } = 3;

		public CaptureTheFlagMode(MultiplayerSession session)
			: base(session)
		{
			RoundDuration = 240f;
		}

		public override MultiplayerModeId Id => MultiplayerModeId.CaptureTheFlag;

		public override string DisplayName => "Capture the Flag";

		public override string Description => "Bring the flag to the start zone. First to " + CapturesToWin + " wins.";

		protected override string FormatScore(float score)
		{
			return score.ToString("0") + (Mathf.Approximately(score, 1f) ? " capture" : " captures");
		}

		protected override void WriteRoundStart(NetWriter w)
		{
			w.Write(CapturesToWin);
		}

		protected override void ReadRoundStart(NetReader r)
		{
			CapturesToWin = r.ReadInt();
		}

		// ------------------------------------------------------------------
		// Round lifecycle
		// ------------------------------------------------------------------

		protected override void OnRoundStarted(int seed)
		{
			m_carrierId = 0;
			m_flagActive = false;
			if (IsHost)
			{
				if (!LocalInLevel)
				{
					Announce("Load a level first, then start the round.");
					return;
				}
				SpawnFlagAtRandom();
				BroadcastFlagState();
			}
			EnsureLevelObjects();
		}

		protected override void OnRoundEnded()
		{
			m_flagActive = false;
			m_carrierId = 0;
			RemoveLevelObjects();
		}

		public override void OnExit()
		{
			base.OnExit();
			RemoveLevelObjects();
		}

		public override void OnLocalLocationChanged()
		{
			RemoveLevelObjects();
			m_haveBase = false;
			m_baseSearched = false;
			if (Phase == RoundPhase.Running && LocalInLevel)
			{
				EnsureLevelObjects();
			}
		}

		public override void OnPlayerLeft(MultiplayerPlayer player)
		{
			base.OnPlayerLeft(player);
			if (IsHost && Phase == RoundPhase.Running && m_carrierId == player.Id)
			{
				Announce(player.Name + " left with the flag; a new flag appears.");
				m_carrierId = 0;
				SpawnFlagAtRandom();
				BroadcastFlagState();
			}
		}

		public override void OnPlayerLocationChanged(MultiplayerPlayer player)
		{
			if (IsHost && Phase == RoundPhase.Running && m_carrierId == player.Id && !player.IsInSameLevel(LocalPlayer))
			{
				m_carrierId = 0;
				SpawnFlagAtRandom();
				BroadcastFlagState();
			}
		}

		public override void OnLocalContraptionStopped()
		{
			if (Phase != RoundPhase.Running || LocalPlayer == null || m_carrierId != LocalPlayer.Id)
			{
				return;
			}
			Vector3 dropPosition = m_lastLocalPigPosition;
			if (IsHost)
			{
				HostDrop(LocalPlayer, dropPosition);
			}
			else
			{
				Send(MsgDropRequest, w => w.Write(dropPosition));
			}
		}

		// ------------------------------------------------------------------
		// Per frame
		// ------------------------------------------------------------------

		public override void Update()
		{
			base.Update();
			if (Phase != RoundPhase.Running || !LocalInLevel)
			{
				return;
			}
			EnsureLevelObjects();
			ContraptionSync sync = ContraptionSync.Instance;
			Transform localPig = null;
			bool haveLocalPig = sync != null && sync.TryGetLocalPig(out localPig);
			if (haveLocalPig)
			{
				m_lastLocalPigPosition = localPig.position;
			}
			// Where is the flag right now?
			Vector3 flagPosition = m_flagPosition;
			if (m_carrierId != 0 && LocalPlayer != null)
			{
				if (m_carrierId == LocalPlayer.Id)
				{
					if (haveLocalPig)
					{
						flagPosition = m_lastLocalPigPosition + Vector3.up * 1.2f;
					}
				}
				else
				{
					RemoteContraption ghost = sync != null ? sync.GetGhost(m_carrierId) : null;
					if (ghost != null && ghost.TryGetLabelPosition(out Vector3 carrierPosition))
					{
						flagPosition = carrierPosition;
					}
				}
				m_flagPosition = flagPosition;
			}
			if (m_flagObject != null)
			{
				bool visible = m_flagActive || m_carrierId != 0;
				if (m_flagObject.activeSelf != visible)
				{
					m_flagObject.SetActive(visible);
				}
				float bob = m_carrierId == 0 ? Mathf.Sin(Time.unscaledTime * 3f) * 0.15f : 0f;
				m_flagObject.transform.position = flagPosition + Vector3.up * bob;
				SphereCollider trigger = m_flagObject.GetComponent<SphereCollider>();
				bool pickable = m_flagActive && m_carrierId == 0;
				if (trigger != null && trigger.enabled != pickable)
				{
					trigger.enabled = pickable;
				}
			}
			// Capture check for the local carrier.
			if (LocalPlayer != null && m_carrierId == LocalPlayer.Id && haveLocalPig && m_haveBase)
			{
				Vector3 pig = m_lastLocalPigPosition;
				Vector2 delta = new Vector2(pig.x - m_basePosition.x, pig.y - m_basePosition.y);
				if (delta.magnitude <= BaseRadius)
				{
					if (IsHost)
					{
						HostCapture(LocalPlayer);
					}
					else
					{
						Send(MsgCaptureRequest);
					}
				}
			}
		}

		// ------------------------------------------------------------------
		// Requests and host authority
		// ------------------------------------------------------------------

		internal void OnFlagTouched(Collider other)
		{
			if (Phase != RoundPhase.Running || !m_flagActive || m_carrierId != 0 || LocalPlayer == null)
			{
				return;
			}
			if (Time.realtimeSinceStartup < m_pickupBlockedUntil)
			{
				return;
			}
			// The trigger may still overlap the vehicle for a frame after the flag moved away
			// (e.g. right after a capture); only accept touches near the flag's real position.
			Vector3 touch = other.transform.position;
			if (Vector2.Distance(new Vector2(touch.x, touch.y), new Vector2(m_flagPosition.x, m_flagPosition.y)) > PickupRadius + 2.5f)
			{
				return;
			}
			Contraption local = ContraptionSync.Instance != null ? ContraptionSync.Instance.TrackedContraption : null;
			if (local == null)
			{
				return;
			}
			BasePart part = other.GetComponent<BasePart>() ?? other.GetComponentInParent<BasePart>();
			if (part == null || part.contraption != local)
			{
				return;
			}
			if (IsHost)
			{
				HostPickup(LocalPlayer);
			}
			else
			{
				Send(MsgPickupRequest);
			}
		}

		private void HostPickup(MultiplayerPlayer player)
		{
			if (!IsHost || Phase != RoundPhase.Running || m_carrierId != 0 || !m_flagActive)
			{
				return;
			}
			if (Time.realtimeSinceStartup < m_hostPickupBlockedUntil)
			{
				return;
			}
			m_carrierId = player.Id;
			m_flagActive = false;
			SnapFlagObject();
			BroadcastFlagState();
			Announce(player.Name + " took the flag!");
		}

		private void HostCapture(MultiplayerPlayer player)
		{
			if (!IsHost || Phase != RoundPhase.Running || m_carrierId != player.Id)
			{
				return;
			}
			AwardPoints(player, 1f);
			Announce(player.Name + " captured the flag!");
			m_carrierId = 0;
			SpawnFlagAtRandom();
			BlockPickups();
			BroadcastFlagState();
			if (GetScore(player.Id) >= CapturesToWin)
			{
				EndRound();
			}
		}

		private void HostDrop(MultiplayerPlayer player, Vector3 position)
		{
			if (!IsHost || Phase != RoundPhase.Running || m_carrierId != player.Id)
			{
				return;
			}
			m_carrierId = 0;
			m_flagPosition = position;
			m_flagActive = true;
			BlockPickups();
			BroadcastFlagState();
			Announce(player.Name + " dropped the flag.");
		}

		/// <summary>Short grace period after the flag was (re)placed, so it cannot be grabbed by the same touch again.</summary>
		private void BlockPickups()
		{
			float until = Time.realtimeSinceStartup + PickupCooldown;
			m_hostPickupBlockedUntil = until;
			m_pickupBlockedUntil = until;
			SnapFlagObject();
		}

		/// <summary>Moves the flag object to its authoritative position immediately (not only in the next Update).</summary>
		private void SnapFlagObject()
		{
			if (m_flagObject != null)
			{
				m_flagObject.transform.position = m_flagPosition;
				SphereCollider trigger = m_flagObject.GetComponent<SphereCollider>();
				if (trigger != null)
				{
					// No trigger while carried: it would ride along on the carrier's vehicle.
					trigger.enabled = m_flagActive && m_carrierId == 0;
				}
			}
		}

		private void BroadcastFlagState()
		{
			int carrier = m_carrierId;
			Vector3 position = m_flagPosition;
			bool active = m_flagActive;
			Send(MsgFlagState, w => w.Write(carrier).Write(position).Write(active));
		}

		protected override void HandleModeMessage(MultiplayerPlayer from, byte subType, NetReader reader)
		{
			switch (subType)
			{
			case MsgFlagState:
			{
				if (!from.IsHost)
				{
					break;
				}
				int carrier = reader.ReadInt();
				Vector3 position = reader.ReadVector3();
				bool active = reader.ReadBool();
				bool becameCarrier = LocalPlayer != null && carrier == LocalPlayer.Id && m_carrierId != carrier;
				bool wasCarried = m_carrierId != 0;
				m_carrierId = carrier;
				m_flagPosition = position;
				m_flagActive = active;
				if (wasCarried && carrier == 0)
				{
					m_pickupBlockedUntil = Time.realtimeSinceStartup + PickupCooldown;
				}
				SnapFlagObject();
				if (becameCarrier)
				{
					Announce("You have the flag! Bring it back to the start zone.");
				}
				break;
			}
			case MsgPickupRequest:
				HostPickup(from);
				break;
			case MsgCaptureRequest:
				HostCapture(from);
				break;
			case MsgDropRequest:
				HostDrop(from, reader.ReadVector3());
				break;
			}
		}

		// ------------------------------------------------------------------
		// Level objects
		// ------------------------------------------------------------------

		public override void OnPlayerJoined(MultiplayerPlayer player)
		{
			base.OnPlayerJoined(player);
			if (IsHost && Phase == RoundPhase.Running)
			{
				int carrier = m_carrierId;
				Vector3 position = m_flagPosition;
				bool active = m_flagActive;
				SendTo(player, MsgFlagState, w => w.Write(carrier).Write(position).Write(active));
			}
		}

		/// <summary>
		/// Places the flag on solid ground at a drivable distance from the start zone.
		/// Candidates are found by casting down onto the level; spots far above or below the
		/// start height (sky islands, pits) are rejected so the flag is always reachable by car.
		/// </summary>
		private void SpawnFlagAtRandom()
		{
			LevelManager levelManager = LevelManager;
			EnsureBase();
			Vector3 origin = m_haveBase ? m_basePosition : Vector3.zero;
			LevelManager.CameraLimits limits = levelManager != null ? levelManager.CurrentCameraLimits : null;
			float levelMinX = float.MinValue;
			float levelMaxX = float.MaxValue;
			if (limits != null && limits.size.x > 1f)
			{
				levelMinX = limits.topLeft.x + limits.size.x * 0.03f;
				levelMaxX = limits.topLeft.x + limits.size.x * 0.97f;
			}
			Vector3 best = Vector3.zero;
			bool found = false;
			for (int attempt = 0; attempt < 40 && !found; attempt++)
			{
				float distance = Random.Range(FlagMinDistance, FlagMaxDistance);
				float side = Random.value < 0.5f ? -1f : 1f;
				float x = origin.x + side * distance;
				if (x < levelMinX || x > levelMaxX)
				{
					x = origin.x - side * distance;
					if (x < levelMinX || x > levelMaxX)
					{
						continue;
					}
				}
				if (TryFindGround(new Vector3(x, origin.y + FlagMaxRise + 30f, origin.z), origin.y, out Vector3 ground))
				{
					best = ground + Vector3.up * FlagHeight;
					found = true;
				}
			}
			if (!found)
			{
				// Fallback: a short, flat drive next to the start.
				best = new Vector3(origin.x + FlagMinDistance, origin.y + FlagHeight, origin.z);
				if (TryFindGround(new Vector3(best.x, origin.y + FlagMaxRise + 30f, origin.z), origin.y, out Vector3 ground))
				{
					best = ground + Vector3.up * FlagHeight;
				}
			}
			m_flagPosition = best;
			m_flagActive = true;
			Debug.Log("[Multiplayer] Flag placed at " + best + ", start zone at " + origin);
		}

		private static bool TryFindGround(Vector3 from, float startHeight, out Vector3 point)
		{
			point = Vector3.zero;
			RaycastHit[] hits = Physics.RaycastAll(from, Vector3.down, FlagMaxRise + FlagMaxDrop + 60f, ~0, QueryTriggerInteraction.Ignore);
			System.Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));
			foreach (RaycastHit hit in hits)
			{
				Collider collider = hit.collider;
				if (collider == null || collider.GetComponentInParent<BasePart>() != null || collider.GetComponentInParent<RemoteContraption>() != null || collider.GetComponentInParent<FlagTrigger>() != null)
				{
					continue;
				}
				float rise = hit.point.y - startHeight;
				if (rise > FlagMaxRise || rise < -FlagMaxDrop)
				{
					return false;
				}
				point = hit.point;
				return true;
			}
			return false;
		}

		private void EnsureBase()
		{
			if (m_haveBase || m_baseSearched)
			{
				return;
			}
			m_baseSearched = true;
			LevelStart start = Object.FindObjectOfType<LevelStart>();
			if (start != null)
			{
				m_basePosition = start.transform.position;
				m_haveBase = true;
				return;
			}
			LevelManager levelManager = LevelManager;
			if (levelManager != null && levelManager.PigStartPosition != Vector3.zero)
			{
				m_basePosition = levelManager.PigStartPosition;
				m_haveBase = true;
			}
		}

		private void EnsureLevelObjects()
		{
			if (!LocalInLevel)
			{
				return;
			}
			EnsureBase();
			if (m_baseObject == null && m_haveBase)
			{
				m_baseObject = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
				m_baseObject.name = "CTF_BaseZone";
				Object.Destroy(m_baseObject.GetComponent<Collider>());
				m_baseObject.transform.position = m_basePosition + Vector3.forward * 0.6f;
				m_baseObject.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
				m_baseObject.transform.localScale = new Vector3(BaseRadius * 2f, 0.05f, BaseRadius * 2f);
				Tint(m_baseObject, new Color(0.2f, 0.9f, 0.3f, 1f));
			}
			if (m_flagObject == null)
			{
				m_flagObject = new GameObject("CTF_Flag");
				// Same physics layer as the vehicle parts, otherwise the layer collision matrix may block the trigger.
				m_flagObject.layer = GuessPartLayer();
				GameObject pole = GameObject.CreatePrimitive(PrimitiveType.Cube);
				pole.name = "Pole";
				Object.Destroy(pole.GetComponent<Collider>());
				pole.transform.SetParent(m_flagObject.transform, false);
				pole.transform.localPosition = new Vector3(0f, 0.9f, 0f);
				pole.transform.localScale = new Vector3(0.15f, 1.8f, 0.15f);
				Tint(pole, new Color(0.85f, 0.85f, 0.85f, 1f));
				GameObject cloth = GameObject.CreatePrimitive(PrimitiveType.Cube);
				cloth.name = "Cloth";
				Object.Destroy(cloth.GetComponent<Collider>());
				cloth.transform.SetParent(m_flagObject.transform, false);
				cloth.transform.localPosition = new Vector3(0.55f, 1.45f, 0f);
				cloth.transform.localScale = new Vector3(1f, 0.6f, 0.05f);
				Tint(cloth, new Color(0.95f, 0.15f, 0.15f, 1f));
				SphereCollider trigger = m_flagObject.AddComponent<SphereCollider>();
				trigger.isTrigger = true;
				trigger.radius = PickupRadius;
				trigger.center = new Vector3(0f, 0.9f, 0f);
				m_flagObject.AddComponent<FlagTrigger>().Mode = this;
				m_flagObject.transform.position = m_flagPosition;
				m_flagObject.SetActive(m_flagActive || m_carrierId != 0);
			}
		}

		private void RemoveLevelObjects()
		{
			if (m_flagObject != null)
			{
				Object.Destroy(m_flagObject);
				m_flagObject = null;
			}
			if (m_baseObject != null)
			{
				Object.Destroy(m_baseObject);
				m_baseObject = null;
			}
		}

		private static int GuessPartLayer()
		{
			try
			{
				GameData gameData = WPFMonoBehaviour.gameData;
				BasePart pig = gameData != null ? gameData.GetCustomPart(BasePart.PartType.Pig, 0) : null;
				if (pig != null)
				{
					return pig.gameObject.layer;
				}
			}
			catch
			{
			}
			return 0;
		}

		private static void Tint(GameObject go, Color color)
		{
			Renderer renderer = go.GetComponent<Renderer>();
			if (renderer != null)
			{
				renderer.material.color = color;
			}
		}

		// ------------------------------------------------------------------
		// GUI
		// ------------------------------------------------------------------

		protected override void DrawModeWindow(MultiplayerUI ui)
		{
			if (!IsHost)
			{
				GUILayout.Label("Captures to win: " + CapturesToWin, ui.Label);
				return;
			}
			GUILayout.BeginHorizontal();
			GUILayout.Label("Captures to win", ui.Label, GUILayout.Width(130f * ui.Scale));
			if (m_capturesField == null)
			{
				m_capturesField = CapturesToWin.ToString();
			}
			GUI.enabled = Phase != RoundPhase.Running;
			m_capturesField = GUILayout.TextField(m_capturesField, 2, ui.TextField, GUILayout.Width(60f * ui.Scale));
			GUI.enabled = true;
			if (int.TryParse(m_capturesField, out int captures) && captures > 0 && Phase != RoundPhase.Running)
			{
				CapturesToWin = captures;
			}
			GUILayout.EndHorizontal();
		}

		protected override void DrawModeHud(MultiplayerUI ui)
		{
			if (Phase != RoundPhase.Running)
			{
				return;
			}
			float width = 260f * ui.Scale;
			float lineHeight = 20f * ui.Scale;
			float x = Screen.width - width - 8f * ui.Scale;
			float y = 8f * ui.Scale + lineHeight * 5f;
			string status;
			if (LocalPlayer != null && m_carrierId == LocalPlayer.Id)
			{
				status = "YOU have the flag - back to the start zone!";
			}
			else if (m_carrierId != 0)
			{
				MultiplayerPlayer carrier = Session.GetPlayer(m_carrierId);
				status = (carrier != null ? carrier.Name : "Someone") + " has the flag";
			}
			else
			{
				status = m_flagActive ? "The flag is up for grabs" : "Waiting for the flag";
			}
			ui.DrawShadowedLabel(new Rect(x, y, width, lineHeight), status, ui.HudBold);
			if (m_flagObject != null && m_flagObject.activeSelf && m_carrierId == 0)
			{
				ui.DrawWorldLabel(m_flagObject.transform.position + Vector3.up * 2.2f, "FLAG");
			}
			if (m_baseObject != null)
			{
				ui.DrawWorldLabel(m_basePosition + Vector3.up * 0.5f, "START ZONE");
			}
		}
	}

	/// <summary>Trigger volume of the flag; forwards touches to the mode.</summary>
	public sealed class FlagTrigger : MonoBehaviour
	{
		public CaptureTheFlagMode Mode { get; set; }

		private void OnTriggerEnter(Collider other)
		{
			Mode?.OnFlagTouched(other);
		}

		private void OnTriggerStay(Collider other)
		{
			Mode?.OnFlagTouched(other);
		}
	}
}
