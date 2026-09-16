using System.Collections.Generic;
using UnityEngine;

namespace BPRE.Fun
{
	/// <summary>
	/// Rainbow rocket: a rocket variant that pushes harder and longer, flashes through all colours,
	/// leaves a rainbow trail and bursts into confetti when it ignites.
	/// Registered as custom rocket part by <see cref="INRuntimeGameData"/>; always unlocked.
	/// </summary>
	public sealed class RainbowRocket : MonoBehaviour
	{
		public const int CustomIndex = 4;

		public const string PartName = "Part_Rocket_05_SET";

		public const string IconName = "Icon_Rocket_05";

		private const float ForceMultiplier = 1.6f;

		private const float DurationMultiplier = 2.5f;

		private const float SpeedMultiplier = 1.5f;

		private const int ConfettiCount = 40;

		private static Material s_vertexColorMaterial;

		private Rocket m_rocket;

		private Renderer m_renderer;

		private TrailRenderer m_trail;

		private bool m_wasFiring;

		public static bool IsRainbowRocket(BasePart part)
		{
			return part != null && part.m_partType == BasePart.PartType.Rocket && part.customPartIndex == CustomIndex;
		}

		/// <summary>Turns a copy of the normal rocket into the rainbow rocket template.</summary>
		public static void SetupTemplate(BasePart part)
		{
			part.gameObject.name = PartName;
			part.customPartIndex = CustomIndex;
			part.m_partTier = BasePart.PartTier.Legendary;
			if (part.GetComponent<RainbowRocket>() == null)
			{
				part.gameObject.AddComponent<RainbowRocket>();
			}
			Renderer renderer = part.GetComponent<Renderer>();
			if (renderer != null)
			{
				renderer.material.color = new Color(1f, 0.55f, 0.9f);
			}
			if (part.m_constructionIconSprite != null)
			{
				part.m_constructionIconSprite.gameObject.name = IconName;
				int i = 0;
				foreach (Renderer iconRenderer in part.m_constructionIconSprite.GetComponentsInChildren<Renderer>(true))
				{
					iconRenderer.material.color = Color.HSVToRGB((0.83f + i * 0.17f) % 1f, 0.45f, 1f);
					i++;
				}
			}
		}

		private void Start()
		{
			m_rocket = GetComponent<Rocket>();
			m_renderer = GetComponent<Renderer>();
			if (m_rocket == null)
			{
				enabled = false;
				return;
			}
			m_rocket.m_boostForce *= ForceMultiplier;
			m_rocket.m_boostDuration *= DurationMultiplier;
			m_rocket.m_maximumSpeed *= SpeedMultiplier;
		}

		private void Update()
		{
			if (m_rocket == null)
			{
				return;
			}
			bool firing = m_rocket.m_enabled;
			if (firing && !m_wasFiring)
			{
				Ignite();
			}
			else if (!firing && m_wasFiring && m_trail != null)
			{
				m_trail.emitting = false;
			}
			m_wasFiring = firing;
			if (m_renderer != null)
			{
				// Slow colour shimmer while parked, fast rainbow while flying.
				float speed = firing ? 2.5f : 0.35f;
				m_renderer.material.color = Color.HSVToRGB(Time.time * speed % 1f, firing ? 0.8f : 0.45f, 1f);
			}
		}

		private void OnDestroy()
		{
			if (m_trail != null)
			{
				Destroy(m_trail.gameObject);
			}
		}

		private void Ignite()
		{
			Material material = VertexColorMaterial();
			if (material == null)
			{
				return;
			}
			if (m_trail == null)
			{
				GameObject trailObject = new GameObject("RainbowTrail");
				trailObject.transform.SetParent(transform, false);
				trailObject.transform.localPosition = new Vector3(0f, 0f, -0.5f);
				m_trail = trailObject.AddComponent<TrailRenderer>();
				m_trail.sharedMaterial = material;
				m_trail.time = 1.2f;
				m_trail.startWidth = 0.9f;
				m_trail.endWidth = 0.1f;
				m_trail.minVertexDistance = 0.15f;
				m_trail.numCapVertices = 4;
				Gradient gradient = new Gradient();
				gradient.SetKeys(new[]
				{
					new GradientColorKey(Color.red, 0f),
					new GradientColorKey(new Color(1f, 0.6f, 0f), 0.2f),
					new GradientColorKey(Color.yellow, 0.4f),
					new GradientColorKey(Color.green, 0.6f),
					new GradientColorKey(new Color(0.2f, 0.5f, 1f), 0.8f),
					new GradientColorKey(new Color(0.6f, 0.2f, 1f), 1f)
				}, new[]
				{
					new GradientAlphaKey(1f, 0f),
					new GradientAlphaKey(0.9f, 0.7f),
					new GradientAlphaKey(0f, 1f)
				});
				m_trail.colorGradient = gradient;
			}
			m_trail.Clear();
			m_trail.emitting = true;
			Confetti.Burst(transform.position + new Vector3(0f, 0f, -1f), ConfettiCount, material);
		}

		internal static Material VertexColorMaterial()
		{
			if (s_vertexColorMaterial != null)
			{
				return s_vertexColorMaterial;
			}
			foreach (string name in new[] { "Sprites/Default", "UI/Default", "_Custom/Unlit_Color_Geometry" })
			{
				Shader shader = Shader.Find(name);
				if (shader != null)
				{
					s_vertexColorMaterial = new Material(shader) { name = "RainbowRocket_" + name };
					return s_vertexColorMaterial;
				}
			}
			return null;
		}
	}

	/// <summary>Cheap confetti: little coloured squares that fly out, fall, spin and fade.</summary>
	public sealed class Confetti : MonoBehaviour
	{
		private const float Lifetime = 1.8f;

		private static Mesh s_quad;

		private readonly List<Piece> m_pieces = new List<Piece>();

		private float m_age;

		private struct Piece
		{
			public Transform Transform;

			public Vector3 Velocity;

			public float Spin;
		}

		public static void Burst(Vector3 position, int count, Material material)
		{
			GameObject root = new GameObject("Confetti");
			root.transform.position = position;
			Confetti confetti = root.AddComponent<Confetti>();
			Mesh quad = QuadMesh();
			for (int i = 0; i < count; i++)
			{
				GameObject piece = new GameObject("Piece");
				piece.transform.SetParent(root.transform, false);
				piece.transform.localScale = Vector3.one * Random.Range(0.25f, 0.45f);
				piece.transform.localRotation = Quaternion.Euler(0f, 0f, Random.Range(0f, 360f));
				piece.AddComponent<MeshFilter>().sharedMesh = quad;
				MeshRenderer renderer = piece.AddComponent<MeshRenderer>();
				renderer.sharedMaterial = material;
				// Vertex colours tint the shared material, so every piece gets its own colour without new materials.
				Mesh coloured = Instantiate(quad);
				Color color = Color.HSVToRGB(Random.value, 0.85f, 1f);
				coloured.colors = new[] { color, color, color, color };
				piece.GetComponent<MeshFilter>().sharedMesh = coloured;
				Vector2 direction = Random.insideUnitCircle.normalized;
				confetti.m_pieces.Add(new Piece
				{
					Transform = piece.transform,
					Velocity = new Vector3(direction.x, Mathf.Abs(direction.y) + 0.3f, 0f) * Random.Range(6f, 14f),
					Spin = Random.Range(-720f, 720f)
				});
			}
		}

		private static Mesh QuadMesh()
		{
			if (s_quad == null)
			{
				s_quad = new Mesh
				{
					name = "ConfettiQuad",
					vertices = new[] { new Vector3(-0.5f, -0.3f, 0f), new Vector3(0.5f, -0.3f, 0f), new Vector3(0.5f, 0.3f, 0f), new Vector3(-0.5f, 0.3f, 0f) },
					uv = new[] { Vector2.zero, Vector2.right, Vector2.one, Vector2.up },
					triangles = new[] { 0, 2, 1, 0, 3, 2 }
				};
				s_quad.colors = new[] { Color.white, Color.white, Color.white, Color.white };
			}
			return s_quad;
		}

		private void Update()
		{
			float dt = Time.deltaTime;
			m_age += dt;
			float scale = Mathf.Clamp01((Lifetime - m_age) / 0.5f);
			for (int i = 0; i < m_pieces.Count; i++)
			{
				Piece piece = m_pieces[i];
				piece.Velocity += new Vector3(0f, -18f * dt, 0f);
				piece.Velocity *= 1f - 1.5f * dt;
				piece.Transform.localPosition += piece.Velocity * dt;
				piece.Transform.Rotate(0f, 0f, piece.Spin * dt);
				piece.Transform.localScale = Vector3.one * 0.35f * scale;
				m_pieces[i] = piece;
			}
			if (m_age >= Lifetime)
			{
				foreach (Piece piece in m_pieces)
				{
					Destroy(piece.Transform.GetComponent<MeshFilter>().sharedMesh);
				}
				Destroy(gameObject);
			}
		}
	}
}
