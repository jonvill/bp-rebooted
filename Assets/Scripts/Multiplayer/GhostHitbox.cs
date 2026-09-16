using UnityEngine;

namespace BPRE.Multiplayer
{
	/// <summary>
	/// Solid, kinematic collider on a ghost part so that local projectiles, explosions and
	/// the local contraption can hit another player's vehicle. Managed by <see cref="GhostSolidity"/>.
	/// </summary>
	public sealed class GhostHitbox : MonoBehaviour
	{
		private const float DefaultPartSize = 0.9f;

		private const float MinSize = 0.3f;

		public RemoteContraption Owner { get; private set; }

		public int PartIndex { get; private set; }

		public IGhostCollisionHandler Handler { get; set; }

		public float LastHitTime { get; set; } = -100f;

		private BoxCollider m_collider;

		/// <summary>Enables or disables the collider (used for spawn protection near the start zone).</summary>
		public void SetSolid(bool solid)
		{
			if (m_collider == null)
			{
				m_collider = GetComponent<BoxCollider>();
			}
			if (m_collider != null && m_collider.enabled != solid)
			{
				m_collider.enabled = solid;
			}
		}

		public static GhostHitbox Attach(RemoteContraption ghost, int partIndex, IGhostCollisionHandler handler)
		{
			Transform part = ghost.GetPart(partIndex);
			if (part == null)
			{
				return null;
			}
			GhostHitbox existing = part.GetComponent<GhostHitbox>();
			if (existing != null)
			{
				existing.Handler = handler;
				return existing;
			}
			Bounds bounds = ComputeLocalBounds(part);
			BoxCollider box = part.gameObject.AddComponent<BoxCollider>();
			box.center = bounds.center;
			box.size = bounds.size;
			box.isTrigger = false;
			Rigidbody body = part.gameObject.AddComponent<Rigidbody>();
			body.isKinematic = true;
			body.useGravity = false;
			body.interpolation = RigidbodyInterpolation.None;
			body.collisionDetectionMode = CollisionDetectionMode.Discrete;
			GhostHitbox hitbox = part.gameObject.AddComponent<GhostHitbox>();
			hitbox.Owner = ghost;
			hitbox.PartIndex = partIndex;
			hitbox.Handler = handler;
			hitbox.m_collider = box;
			return hitbox;
		}

		public static void Detach(RemoteContraption ghost)
		{
			if (ghost == null)
			{
				return;
			}
			foreach (GhostHitbox hitbox in ghost.GetComponentsInChildren<GhostHitbox>(true))
			{
				GameObject go = hitbox.gameObject;
				Object.Destroy(hitbox);
				foreach (BoxCollider collider in go.GetComponents<BoxCollider>())
				{
					Object.Destroy(collider);
				}
				Rigidbody body = go.GetComponent<Rigidbody>();
				if (body != null)
				{
					Object.Destroy(body);
				}
			}
		}

		/// <summary>
		/// Bounds in the part's local space, computed from mesh and sprite data so it also
		/// works while the ghost part is still inactive (renderer bounds are not valid then).
		/// </summary>
		private static Bounds ComputeLocalBounds(Transform part)
		{
			bool any = false;
			Bounds result = new Bounds(Vector3.zero, Vector3.zero);
			foreach (MeshFilter filter in part.GetComponentsInChildren<MeshFilter>(true))
			{
				if (filter.sharedMesh != null)
				{
					Encapsulate(ref result, ref any, part, filter.transform, filter.sharedMesh.bounds);
				}
			}
			foreach (SkinnedMeshRenderer skinned in part.GetComponentsInChildren<SkinnedMeshRenderer>(true))
			{
				if (skinned.sharedMesh != null)
				{
					Encapsulate(ref result, ref any, part, skinned.transform, skinned.sharedMesh.bounds);
				}
			}
			foreach (SpriteRenderer sprite in part.GetComponentsInChildren<SpriteRenderer>(true))
			{
				if (sprite.sprite != null)
				{
					Encapsulate(ref result, ref any, part, sprite.transform, sprite.sprite.bounds);
				}
			}
			Vector3 size = result.size;
			// Runtime generated meshes (Spine) are empty at this point: assume one grid cell.
			if (!any || size.x < MinSize || size.y < MinSize)
			{
				size = new Vector3(Mathf.Max(size.x, DefaultPartSize), Mathf.Max(size.y, DefaultPartSize), Mathf.Max(size.z, MinSize));
				result = new Bounds(any ? result.center : Vector3.zero, size);
			}
			else if (size.z < MinSize)
			{
				result = new Bounds(result.center, new Vector3(size.x, size.y, MinSize));
			}
			return result;
		}

		private static void Encapsulate(ref Bounds result, ref bool any, Transform part, Transform source, Bounds local)
		{
			Vector3 min = local.min;
			Vector3 max = local.max;
			for (int i = 0; i < 8; i++)
			{
				Vector3 corner = new Vector3((i & 1) == 0 ? min.x : max.x, (i & 2) == 0 ? min.y : max.y, (i & 4) == 0 ? min.z : max.z);
				Vector3 inPart = part.InverseTransformPoint(source.TransformPoint(corner));
				if (!any)
				{
					result = new Bounds(inPart, Vector3.zero);
					any = true;
				}
				else
				{
					result.Encapsulate(inPart);
				}
			}
		}

		private void OnCollisionEnter(Collision collision)
		{
			Handler?.OnHitboxCollision(this, collision);
		}
	}
}
