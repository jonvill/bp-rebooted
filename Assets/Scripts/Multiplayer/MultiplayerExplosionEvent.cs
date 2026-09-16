using UnityEngine;

namespace BPRE.Multiplayer
{
	/// <summary>
	/// Sent by TNT and gun projectiles when they explode so that multiplayer modes can
	/// apply splash damage to ghost contraptions (which have no BasePart components).
	/// </summary>
	public struct MultiplayerExplosionEvent : EventManager.Event
	{
		public Vector3 Position;

		public float Radius;

		public float Impulse;

		public static void Send(Vector3 position, float radius, float impulse)
		{
			EventManager.Send(new MultiplayerExplosionEvent
			{
				Position = position,
				Radius = radius,
				Impulse = impulse
			});
		}
	}
}
