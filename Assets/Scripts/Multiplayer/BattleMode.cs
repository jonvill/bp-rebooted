using System.Collections.Generic;
using UnityEngine;

namespace BPRE.Multiplayer
{
	/// <summary>
	/// Ghosts become solid targets. Hitting them with projectiles, explosions or by ramming
	/// damages the owner's real contraption (the owner applies the damage locally).
	/// Points for damage dealt, a bonus for destroying a pig.
	/// </summary>
	public sealed class BattleMode : RoundBasedMode, IGhostCollisionHandler
	{
		private const byte MsgHit = 10;

		private const byte MsgKilled = 11;

		private const float DirectHitDamage = 350f;

		private const float ExplosionDamage = 700f;

		private const float RamDamageFactor = 4f;

		private const float MinRamSpeed = 4f;

		private const float MinDamage = 40f;

		private const float HitCooldown = 0.2f;

		private const float PointsPerDamage = 0.01f;

		private const float KillPoints = 10f;

		private readonly GhostSolidity m_solidity;

		private Contraption m_local;

		private IReadOnlyList<BasePart> m_localParts;

		private bool m_pigDead;

		private int m_savedHpStatus = -1;

		private float m_lastHitDealtTime = -100f;

		private float m_lastHitTakenTime = -100f;

		private string m_lastHitTakenFrom = string.Empty;

		public BattleMode(MultiplayerSession session)
			: base(session)
		{
			RoundDuration = 180f;
			m_solidity = new GhostSolidity(this);
		}

		public override MultiplayerModeId Id => MultiplayerModeId.Battle;

		public override string DisplayName => "Battle";

		public override string Description => "Wreck the others: 1 point per 100 damage, 10 per pig.";

		public override void OnEnter()
		{
			// Part damage must be enabled for hits to have an effect.
			if (GameRules.PartHPStatus == 0)
			{
				m_savedHpStatus = GameRules.PartHPStatus;
				GameRules.SetGameRule("parthpstatus", "2");
			}
			EventManager.Connect<MultiplayerExplosionEvent>(OnExplosion);
		}

		public override void OnExit()
		{
			base.OnExit();
			EventManager.Disconnect<MultiplayerExplosionEvent>(OnExplosion);
			if (m_savedHpStatus >= 0)
			{
				GameRules.SetGameRule("parthpstatus", m_savedHpStatus.ToString());
				m_savedHpStatus = -1;
			}
			m_solidity.ClearAll();
			m_local = null;
			m_localParts = null;
		}

		public override void OnGhostCreated(RemoteContraption ghost)
		{
			m_solidity.Add(ghost);
		}

		public override void OnGhostDestroyed(RemoteContraption ghost)
		{
			m_solidity.Remove(ghost);
		}

		public override void OnLocalContraptionStarted(Contraption contraption, IReadOnlyList<BasePart> parts)
		{
			m_local = contraption;
			m_localParts = parts;
			m_pigDead = false;
		}

		public override void OnLocalContraptionStopped()
		{
			m_local = null;
			m_localParts = null;
		}

		public override void Update()
		{
			base.Update();
			m_solidity.Update(LocalInLevel);
		}

		public override void OnLocalLocationChanged()
		{
			m_solidity.ResetLevel();
		}

		// ------------------------------------------------------------------
		// Dealing damage (local detection)
		// ------------------------------------------------------------------

		public void OnHitboxCollision(GhostHitbox hitbox, Collision collision)
		{
			if (Phase != RoundPhase.Running || hitbox == null || hitbox.Owner == null)
			{
				return;
			}
			if (Time.realtimeSinceStartup - hitbox.LastHitTime < HitCooldown)
			{
				return;
			}
			Collider other = collision.collider;
			if (other == null)
			{
				return;
			}
			if (IsProjectile(other.gameObject))
			{
				hitbox.LastHitTime = Time.realtimeSinceStartup;
				DealDamage(hitbox, DirectHitDamage);
				return;
			}
			BasePart part = other.GetComponent<BasePart>() ?? other.GetComponentInParent<BasePart>();
			if (part == null || m_local == null || part.contraption != m_local)
			{
				return;
			}
			float speed = collision.relativeVelocity.magnitude;
			if (speed < MinRamSpeed)
			{
				return;
			}
			hitbox.LastHitTime = Time.realtimeSinceStartup;
			float damage = speed * speed * RamDamageFactor;
			DealDamage(hitbox, damage);
			// Ramming hurts both sides.
			part.Hurt(Mathf.Max(MinDamage, damage * 0.5f));
		}

		private static bool IsProjectile(GameObject go)
		{
			return go.GetComponentInParent<APProjectile>() != null
				|| go.GetComponentInParent<RailGunProjectile>() != null
				|| go.GetComponentInParent<LightningProjectile>() != null
				|| go.GetComponentInParent<ExplodingGrapplingHookProjectile>() != null;
		}

		private void OnExplosion(MultiplayerExplosionEvent data)
		{
			if (Phase != RoundPhase.Running || data.Radius <= 0f)
			{
				return;
			}
			foreach (GhostHitbox hitbox in m_solidity.Hitboxes)
			{
				if (hitbox == null || !hitbox.gameObject.activeInHierarchy)
				{
					continue;
				}
				float distance = Vector3.Distance(hitbox.transform.position, data.Position);
				if (distance > data.Radius)
				{
					continue;
				}
				float damage = ExplosionDamage * (1f - distance / data.Radius);
				DealDamage(hitbox, damage);
			}
		}

		private void DealDamage(GhostHitbox hitbox, float damage)
		{
			damage = Mathf.Max(MinDamage, damage);
			int targetId = hitbox.Owner.Player.Id;
			int partIndex = hitbox.PartIndex;
			m_lastHitDealtTime = Time.realtimeSinceStartup;
			Send(MsgHit, w => w.Write(targetId).Write(partIndex).Write(damage));
			if (IsHost)
			{
				AwardPoints(LocalPlayer, damage * PointsPerDamage);
			}
		}

		// ------------------------------------------------------------------
		// Taking damage (applied by the owner)
		// ------------------------------------------------------------------

		protected override void HandleModeMessage(MultiplayerPlayer from, byte subType, NetReader reader)
		{
			switch (subType)
			{
			case MsgHit:
			{
				int targetId = reader.ReadInt();
				int partIndex = reader.ReadInt();
				float damage = Mathf.Clamp(reader.ReadFloat(), MinDamage, 5000f);
				if (IsHost && Phase == RoundPhase.Running)
				{
					AwardPoints(from, damage * PointsPerDamage);
				}
				if (LocalPlayer != null && targetId == LocalPlayer.Id)
				{
					ApplyDamage(from, partIndex, damage);
				}
				break;
			}
			case MsgKilled:
			{
				int killerId = reader.ReadInt();
				MultiplayerPlayer killer = Session.GetPlayer(killerId);
				if (IsHost && killer != null)
				{
					AwardPoints(killer, KillPoints);
				}
				Announce(from.Name + " was destroyed by " + (killer != null ? killer.Name : "someone") + "!");
				break;
			}
			}
		}

		private void ApplyDamage(MultiplayerPlayer from, int partIndex, float damage)
		{
			if (Phase != RoundPhase.Running || m_local == null || m_localParts == null)
			{
				return;
			}
			if (partIndex < 0 || partIndex >= m_localParts.Count)
			{
				return;
			}
			BasePart part = m_localParts[partIndex];
			if (part == null)
			{
				return;
			}
			m_lastHitTakenTime = Time.realtimeSinceStartup;
			m_lastHitTakenFrom = from.Name;
			part.Hurt(damage);
			if (m_pigDead)
			{
				return;
			}
			BasePart pig = m_local != null ? m_local.FindPig() : null;
			if (pig == null || pig.m_hp <= 0f)
			{
				m_pigDead = true;
				int killerId = from.Id;
				Send(MsgKilled, w => w.Write(killerId));
				if (IsHost)
				{
					AwardPoints(from, KillPoints);
				}
				Announce("Your pig was destroyed by " + from.Name + "!");
			}
		}

		// ------------------------------------------------------------------
		// GUI
		// ------------------------------------------------------------------

		protected override void DrawModeWindow(MultiplayerUI ui)
		{
			GUILayout.Label("Guns, TNT, rockets and ramming count.", ui.Label);
		}

		protected override void DrawModeHud(MultiplayerUI ui)
		{
			float now = Time.realtimeSinceStartup;
			float width = 260f * ui.Scale;
			float lineHeight = 22f * ui.Scale;
			float x = Screen.width - width - 8f * ui.Scale;
			float y = 8f * ui.Scale + lineHeight * 5f;
			if (now - m_lastHitDealtTime < 0.6f)
			{
				ui.DrawShadowedLabel(new Rect(x, y, width, lineHeight), "HIT!", ui.HudBold);
				y += lineHeight;
			}
			if (now - m_lastHitTakenTime < 1f)
			{
				ui.DrawShadowedLabel(new Rect(x, y, width, lineHeight), "Hit by " + m_lastHitTakenFrom + "!", ui.HudAlert);
			}
		}
	}
}
