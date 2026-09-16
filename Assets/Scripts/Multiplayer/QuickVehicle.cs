using System.Collections;
using UnityEngine;

namespace BPRE.Multiplayer
{
	/// <summary>
	/// Starter car for multiplayer rounds: a wooden frame with pig, engine, gearbox and two
	/// motor wheels, placed through the game's construction UI. "Start" presses play and
	/// switches the engine on; "Reverse" toggles the gearbox while driving.
	/// </summary>
	public static class QuickVehicle
	{
		public static bool CanBuild
		{
			get
			{
				LevelManager levelManager = WPFMonoBehaviour.levelManager;
				return levelManager != null && levelManager.ConstructionUI != null && levelManager.gameState == LevelManager.GameState.Building;
			}
		}

		public static bool IsRunning => ContraptionSync.Instance != null && ContraptionSync.Instance.TrackedContraption != null;

		public static bool Build(out string error)
		{
			LevelManager levelManager = WPFMonoBehaviour.levelManager;
			if (levelManager == null || levelManager.ConstructionUI == null)
			{
				error = "Not in a level.";
				return false;
			}
			if (levelManager.gameState != LevelManager.GameState.Building)
			{
				error = "Go back to building first.";
				return false;
			}
			GameData gameData = WPFMonoBehaviour.gameData;
			if (gameData == null)
			{
				error = "Game data not available.";
				return false;
			}
			ConstructionUI ui = levelManager.ConstructionUI;
			ui.ClearContraption();
			// Frames first, then the parts that sit inside them, then the wheels below.
			Place(ui, gameData, BasePart.PartType.WoodenFrame, -1, 1);
			Place(ui, gameData, BasePart.PartType.WoodenFrame, 0, 1);
			Place(ui, gameData, BasePart.PartType.WoodenFrame, 1, 1);
			Place(ui, gameData, BasePart.PartType.WoodenFrame, 2, 1);
			Place(ui, gameData, BasePart.PartType.Engine, 0, 1);
			Place(ui, gameData, BasePart.PartType.Pig, 1, 1);
			Place(ui, gameData, BasePart.PartType.Gearbox, 2, 1);
			BasePart wheelA = Place(ui, gameData, BasePart.PartType.MotorWheel, -1, 0);
			BasePart wheelB = Place(ui, gameData, BasePart.PartType.MotorWheel, 2, 0);
			if (wheelA == null || wheelB == null)
			{
				error = "Some parts are not available in this level.";
				return false;
			}
			error = null;
			return true;
		}

		private static BasePart Place(ConstructionUI ui, GameData gameData, BasePart.PartType type, int x, int y)
		{
			BasePart prefab = gameData.GetCustomPart(type, 0);
			if (prefab == null)
			{
				Debug.LogWarning("[Multiplayer] QuickVehicle: no prefab for " + type);
				return null;
			}
			BasePart part = ui.SetPartAt(x, y, prefab, autoalign: false);
			if (part != null)
			{
				part.SetRotation(BasePart.GridRotation.Deg_0);
			}
			return part;
		}

		/// <summary>Presses play and switches the engine on shortly after the contraption started.</summary>
		public static void StartAndDrive(MonoBehaviour runner)
		{
			LevelManager levelManager = WPFMonoBehaviour.levelManager;
			if (levelManager == null)
			{
				return;
			}
			if (levelManager.gameState == LevelManager.GameState.Building)
			{
				EventManager.Send(new UIEvent(UIEvent.Type.Play));
			}
			runner.StartCoroutine(EngineOn());
		}

		private static IEnumerator EngineOn()
		{
			float deadline = Time.realtimeSinceStartup + 3f;
			while (Time.realtimeSinceStartup < deadline)
			{
				Contraption contraption = ContraptionSync.Instance != null ? ContraptionSync.Instance.TrackedContraption : null;
				if (contraption != null)
				{
					yield return new WaitForSecondsRealtime(0.3f);
					if (contraption != null)
					{
						contraption.ActivatePartType(BasePart.PartType.Engine, BasePart.Direction.Right);
					}
					yield break;
				}
				yield return null;
			}
		}

		/// <summary>Toggles the gearbox of the running contraption (reverses the motor wheels).</summary>
		public static bool ToggleReverse()
		{
			Contraption contraption = ContraptionSync.Instance != null ? ContraptionSync.Instance.TrackedContraption : null;
			if (contraption == null)
			{
				return false;
			}
			contraption.ActivatePartType(BasePart.PartType.Gearbox, BasePart.Direction.Right);
			return true;
		}

		/// <summary>Stops the running contraption and returns to building.</summary>
		public static void Stop()
		{
			LevelManager levelManager = WPFMonoBehaviour.levelManager;
			if (levelManager != null && levelManager.gameState != LevelManager.GameState.Building)
			{
				EventManager.Send(new UIEvent(UIEvent.Type.Building));
			}
		}
	}
}
