using System.Collections.Generic;
using UnityEngine;

namespace BPRE.Multiplayer
{
	/// <summary>Receives collisions reported by a ghost hitbox.</summary>
	public interface IGhostCollisionHandler
	{
		void OnHitboxCollision(GhostHitbox hitbox, Collision collision);
	}

	/// <summary>
	/// Makes other players' ghosts physically solid, so the local contraption can drive onto,
	/// push against or ram them. Shared by the modes that want collisions (Free Play, Battle).
	///
	/// Ghost parts are kinematic: they follow the snapshots of their owner and are never moved
	/// by the local physics. Each player therefore sees a slightly different outcome of a
	/// collision - only the owner's own vehicle is authoritative for that vehicle.
	/// </summary>
	public sealed class GhostSolidity
	{
		private readonly List<GhostHitbox> m_hitboxes = new List<GhostHitbox>();

		private readonly IGhostCollisionHandler m_handler;

		private Vector3 m_startPosition;

		private bool m_haveStart;

		private bool m_startSearched;

		/// <summary>Ghosts closer than this to the start zone stay soft, so everyone can leave the shared spawn.</summary>
		public float SpawnProtectionRadius { get; set; } = 6f;

		/// <summary>Turns the whole feature on or off; off removes every hitbox.</summary>
		public bool Enabled { get; private set; } = true;

		public IReadOnlyList<GhostHitbox> Hitboxes => m_hitboxes;

		public GhostSolidity(IGhostCollisionHandler handler)
		{
			m_handler = handler;
		}

		public void SetEnabled(bool enabled, IEnumerable<RemoteContraption> currentGhosts)
		{
			if (Enabled == enabled)
			{
				return;
			}
			Enabled = enabled;
			if (enabled)
			{
				if (currentGhosts != null)
				{
					foreach (RemoteContraption ghost in currentGhosts)
					{
						Add(ghost);
					}
				}
			}
			else
			{
				ClearAll();
			}
		}

		public void Add(RemoteContraption ghost)
		{
			if (!Enabled || ghost == null)
			{
				return;
			}
			for (int i = 0; i < ghost.PartCount; i++)
			{
				GhostHitbox hitbox = GhostHitbox.Attach(ghost, i, m_handler);
				if (hitbox != null && !m_hitboxes.Contains(hitbox))
				{
					m_hitboxes.Add(hitbox);
				}
			}
		}

		public void Remove(RemoteContraption ghost)
		{
			m_hitboxes.RemoveAll(h => h == null || h.Owner == ghost);
		}

		public void ClearAll()
		{
			foreach (GhostHitbox hitbox in m_hitboxes)
			{
				if (hitbox != null)
				{
					GhostHitbox.Detach(hitbox.Owner);
				}
			}
			m_hitboxes.Clear();
		}

		/// <summary>Call once per frame while inside a level.</summary>
		public void Update(bool inLevel)
		{
			m_hitboxes.RemoveAll(h => h == null);
			if (!Enabled || !inLevel)
			{
				return;
			}
			EnsureStart();
			foreach (GhostHitbox hitbox in m_hitboxes)
			{
				bool solid = true;
				if (m_haveStart && hitbox.Owner != null && hitbox.Owner.TryGetLabelPosition(out Vector3 position))
				{
					float distance = Vector2.Distance(new Vector2(position.x, position.y), new Vector2(m_startPosition.x, m_startPosition.y));
					solid = distance > SpawnProtectionRadius;
				}
				hitbox.SetSolid(solid);
			}
		}

		/// <summary>Forget the cached start zone after a level change.</summary>
		public void ResetLevel()
		{
			m_startSearched = false;
			m_haveStart = false;
		}

		private void EnsureStart()
		{
			if (m_startSearched)
			{
				return;
			}
			m_startSearched = true;
			LevelStart start = Object.FindObjectOfType<LevelStart>();
			if (start != null)
			{
				m_startPosition = start.transform.position;
				m_haveStart = true;
				return;
			}
			LevelManager levelManager = WPFMonoBehaviour.levelManager;
			if (levelManager != null && levelManager.PigStartPosition != Vector3.zero)
			{
				m_startPosition = levelManager.PigStartPosition;
				m_haveStart = true;
			}
		}
	}
}
