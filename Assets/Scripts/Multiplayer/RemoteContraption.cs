using System;
using System.Collections.Generic;
using UnityEngine;

namespace BPRE.Multiplayer
{
	/// <summary>
	/// Visual-only copy of another player's contraption. Built from the part prefabs
	/// with all physics and gameplay scripts stripped, then driven by snapshots.
	/// </summary>
	public sealed class RemoteContraption : MonoBehaviour
	{
		public struct PartUnit
		{
			public int X;

			public int Y;

			public int PartType;

			public int CustomIndex;

			public int Rotation;

			public bool Flipped;

			public float OffsetX;

			public float OffsetY;

			public Vector3 Scale;
		}

		private const float MinInterval = 0.02f;

		private const float MaxInterval = 0.5f;

		private readonly List<Transform> m_parts = new List<Transform>();

		private Vector3[] m_fromPos;

		private Vector3[] m_toPos;

		private Quaternion[] m_fromRot;

		private Quaternion[] m_toRot;

		private bool[] m_visible;

		private float m_lerp;

		private float m_interval = 1f / NetProtocol.StateSendRate;

		private float m_lastSnapshotTime = -1f;

		private int m_pigIndex = -1;

		public MultiplayerPlayer Player { get; private set; }

		public bool HasSnapshot { get; private set; }

		public int PartCount => m_parts.Count;

		public int PigIndex => m_pigIndex;

		public Transform GetPart(int index)
		{
			return index >= 0 && index < m_parts.Count ? m_parts[index] : null;
		}

		public bool IsPartVisible(int index)
		{
			return HasSnapshot && index >= 0 && index < m_parts.Count && m_visible[index];
		}

		public static RemoteContraption Create(MultiplayerPlayer player, List<PartUnit> units)
		{
			GameObject root = new GameObject("RemoteContraption_" + player.Name);
			root.SetActive(false);
			RemoteContraption remote = root.AddComponent<RemoteContraption>();
			remote.Player = player;
			GameData gameData = WPFMonoBehaviour.gameData;
			for (int i = 0; i < units.Count; i++)
			{
				PartUnit unit = units[i];
				GameObject go = null;
				if (unit.PartType >= 0 && gameData != null)
				{
					try
					{
						BasePart prefab = gameData.GetCustomPart((BasePart.PartType)unit.PartType, unit.CustomIndex);
						if (prefab == null && unit.CustomIndex != 0)
						{
							prefab = gameData.GetCustomPart((BasePart.PartType)unit.PartType, 0);
						}
						if (prefab != null)
						{
							go = UnityEngine.Object.Instantiate(prefab.gameObject, root.transform);
							StripGameplay(go);
							go.transform.localScale = unit.Scale == Vector3.zero ? Vector3.one : unit.Scale;
						}
					}
					catch (Exception ex)
					{
						Debug.LogWarning("[Multiplayer] Ghost part " + unit.PartType + " fell back to a placeholder: " + ex.Message);
						if (go != null)
						{
							UnityEngine.Object.DestroyImmediate(go);
							go = null;
						}
					}
				}
				if (go == null)
				{
					go = GameObject.CreatePrimitive(PrimitiveType.Cube);
					Collider collider = go.GetComponent<Collider>();
					if (collider != null)
					{
						UnityEngine.Object.DestroyImmediate(collider);
					}
					go.transform.SetParent(root.transform, worldPositionStays: false);
					go.transform.localScale = Vector3.one * 0.8f;
				}
				go.name = "Part_" + i;
				go.SetActive(false);
				remote.m_parts.Add(go.transform);
				if (unit.PartType == (int)BasePart.PartType.Pig && remote.m_pigIndex < 0)
				{
					remote.m_pigIndex = i;
				}
			}
			int count = remote.m_parts.Count;
			remote.m_fromPos = new Vector3[count];
			remote.m_toPos = new Vector3[count];
			remote.m_fromRot = new Quaternion[count];
			remote.m_toRot = new Quaternion[count];
			remote.m_visible = new bool[count];
			root.SetActive(true);
			return remote;
		}

		/// <summary>
		/// Removes everything that simulates or reacts, keeping only what draws.
		/// Order matters because of component dependencies (scripts may require a
		/// rigidbody, joints require a rigidbody, particle systems require their renderer).
		/// </summary>
		private static void StripGameplay(GameObject go)
		{
			SetTagRecursive(go.transform, "Untagged");
			for (int pass = 0; pass < 3; pass++)
			{
				bool removed = false;
				// Reverse order: components that require another one are added after it,
				// so removing back to front avoids "can't remove X because Y depends on it".
				MonoBehaviour[] behaviours = go.GetComponentsInChildren<MonoBehaviour>(true);
				for (int i = behaviours.Length - 1; i >= 0; i--)
				{
					MonoBehaviour behaviour = behaviours[i];
					if (behaviour != null && !ShouldKeep(behaviour))
					{
						UnityEngine.Object.DestroyImmediate(behaviour);
						removed = true;
					}
				}
				removed |= DestroyAll<Joint>(go);
				removed |= DestroyAll<Joint2D>(go);
				removed |= DestroyAll<Rigidbody>(go);
				removed |= DestroyAll<Rigidbody2D>(go);
				removed |= DestroyAll<Collider>(go);
				removed |= DestroyAll<Collider2D>(go);
				removed |= DestroyAll<AudioSource>(go);
				removed |= DestroyAll<ParticleSystem>(go);
				removed |= DestroyAll<ParticleSystemRenderer>(go);
				removed |= DestroyAll<Light>(go);
				if (!removed)
				{
					break;
				}
			}
		}

		private static bool DestroyAll<T>(GameObject go) where T : Component
		{
			T[] components = go.GetComponentsInChildren<T>(true);
			for (int i = 0; i < components.Length; i++)
			{
				if (components[i] != null)
				{
					UnityEngine.Object.DestroyImmediate(components[i]);
				}
			}
			return components.Length > 0;
		}

		private static bool ShouldKeep(MonoBehaviour behaviour)
		{
			string ns = behaviour.GetType().Namespace ?? string.Empty;
			return ns.StartsWith("Spine", StringComparison.Ordinal) || ns.StartsWith("TMPro", StringComparison.Ordinal);
		}

		private static void SetTagRecursive(Transform t, string tag)
		{
			t.gameObject.tag = tag;
			for (int i = 0; i < t.childCount; i++)
			{
				SetTagRecursive(t.GetChild(i), tag);
			}
		}

		public void ApplySnapshot(NetReader reader)
		{
			float time = reader.ReadFloat();
			int count = reader.ReadInt();
			if (count != m_parts.Count)
			{
				return;
			}
			for (int i = 0; i < count; i++)
			{
				bool visible = reader.ReadBool();
				m_visible[i] = visible;
				if (!visible)
				{
					continue;
				}
				Vector3 position = reader.ReadVector3();
				Quaternion rotation = reader.ReadQuaternion();
				if (!HasSnapshot || m_parts[i] == null)
				{
					m_fromPos[i] = position;
					m_fromRot[i] = rotation;
				}
				else
				{
					m_fromPos[i] = m_parts[i].position;
					m_fromRot[i] = m_parts[i].rotation;
				}
				m_toPos[i] = position;
				m_toRot[i] = rotation;
			}
			if (m_lastSnapshotTime >= 0f)
			{
				m_interval = Mathf.Clamp(time - m_lastSnapshotTime, MinInterval, MaxInterval);
			}
			m_lastSnapshotTime = time;
			m_lerp = 0f;
			HasSnapshot = true;
		}

		private void Update()
		{
			if (!HasSnapshot)
			{
				return;
			}
			m_lerp = Mathf.Min(1f, m_lerp + Time.unscaledDeltaTime / m_interval);
			for (int i = 0; i < m_parts.Count; i++)
			{
				Transform part = m_parts[i];
				if (part == null)
				{
					continue;
				}
				bool visible = m_visible[i];
				if (part.gameObject.activeSelf != visible)
				{
					part.gameObject.SetActive(visible);
				}
				if (visible)
				{
					part.position = Vector3.Lerp(m_fromPos[i], m_toPos[i], m_lerp);
					part.rotation = Quaternion.Slerp(m_fromRot[i], m_toRot[i], m_lerp);
				}
			}
		}

		/// <summary>World position to hang the name tag on: above the pig, or above the centre of the visible parts.</summary>
		public bool TryGetLabelPosition(out Vector3 position)
		{
			position = Vector3.zero;
			if (!HasSnapshot)
			{
				return false;
			}
			if (m_pigIndex >= 0 && m_pigIndex < m_parts.Count && m_visible[m_pigIndex] && m_parts[m_pigIndex] != null)
			{
				position = m_parts[m_pigIndex].position + Vector3.up * 1.5f;
				return true;
			}
			Vector3 sum = Vector3.zero;
			int visibleCount = 0;
			for (int i = 0; i < m_parts.Count; i++)
			{
				if (m_visible[i] && m_parts[i] != null)
				{
					sum += m_parts[i].position;
					visibleCount++;
				}
			}
			if (visibleCount == 0)
			{
				return false;
			}
			position = sum / visibleCount + Vector3.up * 1.5f;
			return true;
		}
	}
}
