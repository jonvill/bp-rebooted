using System;
using System.Collections.Generic;
using UnityEngine;

namespace BPRE.Multiplayer
{
	/// <summary>
	/// Broadcasts the locally running contraption and mirrors the contraptions of
	/// other players as ghosts. Physics is simulated only by the owner; everybody
	/// else receives part transforms.
	/// </summary>
	public sealed class ContraptionSync : MonoBehaviour
	{
		public static ContraptionSync Instance { get; private set; }

		private MultiplayerSession m_session;

		private Contraption m_tracked;

		private bool m_tracking;

		private List<BasePart> m_trackedParts;

		private float m_nextSendTime;

		private readonly Dictionary<int, RemoteContraption> m_ghosts = new Dictionary<int, RemoteContraption>();

		public IEnumerable<RemoteContraption> Ghosts
		{
			get
			{
				foreach (RemoteContraption ghost in m_ghosts.Values)
				{
					if (ghost != null)
					{
						yield return ghost;
					}
				}
			}
		}

		public bool IsBroadcasting => m_tracking;

		/// <summary>The local contraption currently being replicated, or null.</summary>
		public Contraption TrackedContraption => m_tracking ? m_tracked : null;

		/// <summary>Parts of the tracked contraption in replication order (entries may be null once destroyed).</summary>
		public IReadOnlyList<BasePart> TrackedParts => m_tracking ? m_trackedParts : null;

		private void Awake()
		{
			Instance = this;
		}

		private void Start()
		{
			m_session = MultiplayerSession.Instance;
			if (m_session == null)
			{
				return;
			}
			m_session.ContraptionMessage += OnContraptionMessage;
			m_session.PlayerLeft += OnPlayerLeft;
			m_session.PlayerLocationChanged += OnPlayerLocationChanged;
			m_session.LocalLocationChanged += OnLocalLocationChanged;
			m_session.SessionEnded += OnSessionEnded;
			m_session.ModeChanged += OnModeChanged;
		}

		private void OnDestroy()
		{
			if (m_session != null)
			{
				m_session.ContraptionMessage -= OnContraptionMessage;
				m_session.PlayerLeft -= OnPlayerLeft;
				m_session.PlayerLocationChanged -= OnPlayerLocationChanged;
				m_session.LocalLocationChanged -= OnLocalLocationChanged;
				m_session.SessionEnded -= OnSessionEnded;
				m_session.ModeChanged -= OnModeChanged;
			}
			if (Instance == this)
			{
				Instance = null;
			}
		}

		private void Update()
		{
			if (m_session == null || !m_session.IsActive)
			{
				if (m_tracking)
				{
					StopTracking(sendStop: false);
				}
				return;
			}
			if (m_session.LocalPlayer == null || !m_session.LocalPlayer.InLevel)
			{
				// Outside of a level there is nothing to replicate; also avoids scene searches every frame.
				if (m_tracking)
				{
					StopTracking(sendStop: true);
				}
				return;
			}
			Contraption running = FindRunningContraption();
			if (m_tracking && (running == null || running != m_tracked))
			{
				StopTracking(sendStop: true);
			}
			if (!m_tracking && running != null)
			{
				StartTracking(running);
			}
			if (m_tracking && Time.unscaledTime >= m_nextSendTime)
			{
				SendState();
				m_nextSendTime = Time.unscaledTime + 1f / NetProtocol.StateSendRate;
			}
		}

		private static Contraption FindRunningContraption()
		{
			LevelManager levelManager = WPFMonoBehaviour.levelManager;
			if (levelManager == null)
			{
				return null;
			}
			GameMode mode = levelManager.CurrentGameMode;
			if (mode == null)
			{
				return null;
			}
			Contraption running = mode.ContraptionRunning;
			return running != null ? running : null;
		}

		public RemoteContraption GetGhost(int playerId)
		{
			return m_ghosts.TryGetValue(playerId, out RemoteContraption ghost) && ghost != null ? ghost : null;
		}

		/// <summary>Transform of the pig of the local running contraption, if any.</summary>
		public bool TryGetLocalPig(out Transform pig)
		{
			pig = null;
			if (!m_tracking || m_tracked == null)
			{
				return false;
			}
			BasePart part = m_tracked.FindPig();
			if (part == null)
			{
				return false;
			}
			pig = part.transform;
			return true;
		}

		// ------------------------------------------------------------------
		// Outgoing
		// ------------------------------------------------------------------

		private void StartTracking(Contraption contraption)
		{
			List<BasePart> parts = contraption.Parts;
			if (parts == null || parts.Count == 0)
			{
				return;
			}
			m_tracked = contraption;
			m_trackedParts = new List<BasePart>(parts);
			m_tracking = true;
			m_nextSendTime = 0f;
			byte[] start = BuildStart();
			m_session.LocalPlayer.LastContraptionStart = start;
			m_session.SendToAll(start);
			m_session.CurrentMode?.OnLocalContraptionStarted(contraption, m_trackedParts);
		}

		private void StopTracking(bool sendStop)
		{
			bool wasTracking = m_tracking;
			m_tracking = false;
			m_tracked = null;
			m_trackedParts = null;
			if (m_session != null && m_session.LocalPlayer != null)
			{
				m_session.LocalPlayer.LastContraptionStart = null;
			}
			if (sendStop && m_session != null && m_session.IsActive)
			{
				using (NetWriter writer = new NetWriter(NetMessageType.ContraptionStop))
				{
					writer.Write(m_session.LocalPlayer.Id);
					m_session.SendToAll(writer.ToArray());
				}
			}
			if (wasTracking)
			{
				m_session?.CurrentMode?.OnLocalContraptionStopped();
			}
		}

		private byte[] BuildStart()
		{
			using (NetWriter writer = new NetWriter(NetMessageType.ContraptionStart))
			{
				writer.Write(m_session.LocalPlayer.Id);
				writer.Write(m_trackedParts.Count);
				foreach (BasePart part in m_trackedParts)
				{
					if (part == null)
					{
						writer.Write(0).Write(0).Write(-1).Write(0).Write(0).Write(false).Write(0f).Write(0f).Write(Vector3.one);
						continue;
					}
					writer.Write(part.m_coordX)
						.Write(part.m_coordY)
						.Write((int)part.m_partType)
						.Write(part.customPartIndex)
						.Write((int)part.m_gridRotation)
						.Write(part.m_flipped)
						.Write(part.offsetX)
						.Write(part.offsetY)
						.Write(part.transform.localScale);
				}
				return writer.ToArray();
			}
		}

		private void SendState()
		{
			using (NetWriter writer = new NetWriter(NetMessageType.ContraptionState))
			{
				writer.Write(m_session.LocalPlayer.Id);
				writer.Write(Time.unscaledTime);
				writer.Write(m_trackedParts.Count);
				foreach (BasePart part in m_trackedParts)
				{
					bool visible = part != null && part.gameObject.activeInHierarchy;
					writer.Write(visible);
					if (visible)
					{
						Transform t = part.transform;
						writer.Write(t.position).Write(t.rotation);
					}
				}
				m_session.SendToAll(writer.ToArray());
			}
		}

		// ------------------------------------------------------------------
		// Incoming
		// ------------------------------------------------------------------

		private void OnContraptionMessage(MultiplayerPlayer player, NetReader reader)
		{
			reader.ReadInt();
			switch (reader.Type)
			{
			case NetMessageType.ContraptionStart:
				HandleStart(player, reader);
				break;
			case NetMessageType.ContraptionState:
				if (m_ghosts.TryGetValue(player.Id, out RemoteContraption ghost) && ghost != null)
				{
					ghost.ApplySnapshot(reader);
				}
				break;
			case NetMessageType.ContraptionStop:
				DestroyGhost(player.Id);
				break;
			}
		}

		private void HandleStart(MultiplayerPlayer player, NetReader reader)
		{
			DestroyGhost(player.Id);
			if (m_session.LocalPlayer == null || !player.IsInSameLevel(m_session.LocalPlayer))
			{
				return;
			}
			int count = reader.ReadInt();
			if (count <= 0 || count > 4096)
			{
				return;
			}
			List<RemoteContraption.PartUnit> units = new List<RemoteContraption.PartUnit>(count);
			for (int i = 0; i < count; i++)
			{
				units.Add(new RemoteContraption.PartUnit
				{
					X = reader.ReadInt(),
					Y = reader.ReadInt(),
					PartType = reader.ReadInt(),
					CustomIndex = reader.ReadInt(),
					Rotation = reader.ReadInt(),
					Flipped = reader.ReadBool(),
					OffsetX = reader.ReadFloat(),
					OffsetY = reader.ReadFloat(),
					Scale = reader.ReadVector3()
				});
			}
			try
			{
				RemoteContraption ghost = RemoteContraption.Create(player, units);
				m_ghosts[player.Id] = ghost;
				m_session.CurrentMode?.OnGhostCreated(ghost);
			}
			catch (Exception ex)
			{
				Debug.LogWarning("[Multiplayer] Could not build ghost for " + player.Name + ": " + ex.Message);
			}
		}

		private void RebuildGhostFromCache(MultiplayerPlayer player)
		{
			if (player == null || player.IsLocal || player.LastContraptionStart == null)
			{
				return;
			}
			try
			{
				using (NetReader reader = new NetReader(player.LastContraptionStart))
				{
					reader.ReadInt();
					HandleStart(player, reader);
				}
			}
			catch (Exception ex)
			{
				Debug.LogWarning("[Multiplayer] Could not restore ghost for " + player.Name + ": " + ex.Message);
			}
		}

		private void OnPlayerLeft(MultiplayerPlayer player)
		{
			DestroyGhost(player.Id);
		}

		private void OnPlayerLocationChanged(MultiplayerPlayer player)
		{
			if (m_session.LocalPlayer != null && player.IsInSameLevel(m_session.LocalPlayer))
			{
				RebuildGhostFromCache(player);
			}
			else
			{
				DestroyGhost(player.Id);
			}
		}

		private void OnLocalLocationChanged()
		{
			DestroyAllGhosts();
			MultiplayerPlayer local = m_session.LocalPlayer;
			if (local == null || !local.InLevel)
			{
				return;
			}
			foreach (MultiplayerPlayer player in m_session.Players)
			{
				if (!player.IsLocal && player.IsInSameLevel(local))
				{
					RebuildGhostFromCache(player);
				}
			}
		}

		private void OnModeChanged(MultiplayerGameMode mode)
		{
			// Let the new mode decorate existing ghosts (e.g. add hitboxes) and know about our contraption.
			if (mode == null)
			{
				return;
			}
			foreach (RemoteContraption ghost in Ghosts)
			{
				mode.OnGhostCreated(ghost);
			}
			if (m_tracking && m_tracked != null)
			{
				mode.OnLocalContraptionStarted(m_tracked, m_trackedParts);
			}
		}

		private void OnSessionEnded()
		{
			DestroyAllGhosts();
			m_tracking = false;
			m_tracked = null;
			m_trackedParts = null;
		}

		private void DestroyGhost(int playerId)
		{
			if (m_ghosts.TryGetValue(playerId, out RemoteContraption ghost))
			{
				if (ghost != null)
				{
					m_session.CurrentMode?.OnGhostDestroyed(ghost);
					UnityEngine.Object.Destroy(ghost.gameObject);
				}
				m_ghosts.Remove(playerId);
			}
		}

		private void DestroyAllGhosts()
		{
			foreach (RemoteContraption ghost in m_ghosts.Values)
			{
				if (ghost != null)
				{
					m_session.CurrentMode?.OnGhostDestroyed(ghost);
					UnityEngine.Object.Destroy(ghost.gameObject);
				}
			}
			m_ghosts.Clear();
		}
	}
}
