using System.Collections.Generic;
using UnityEngine;

namespace BPRE.Multiplayer
{
	/// <summary>
	/// Competition: who gets the pig the furthest (or highest) from the start within a round.
	/// Every attempt during the round counts; the best one is kept.
	/// </summary>
	public sealed class DistanceMode : RoundBasedMode
	{
		public enum Metric : byte
		{
			Distance = 0,
			Height = 1
		}

		private Contraption m_contraption;

		private Vector3 m_start;

		private float m_bestThisRound;

		public Metric CurrentMetric { get; private set; } = Metric.Distance;

		public DistanceMode(MultiplayerSession session)
			: base(session)
		{
		}

		public override MultiplayerModeId Id => MultiplayerModeId.Distance;

		public override string DisplayName => "Distance";

		public override string Description => CurrentMetric == Metric.Height
			? "Highest pig wins. Best attempt counts."
			: "Furthest pig wins. Best attempt counts.";

		protected override string FormatScore(float score)
		{
			return score.ToString("0.0") + " m";
		}

		protected override void WriteRoundStart(NetWriter w)
		{
			w.Write((byte)CurrentMetric);
		}

		protected override void ReadRoundStart(NetReader r)
		{
			CurrentMetric = (Metric)r.ReadByte();
		}

		protected override void OnRoundStarted(int seed)
		{
			m_bestThisRound = 0f;
		}

		public override void OnLocalContraptionStarted(Contraption contraption, IReadOnlyList<BasePart> parts)
		{
			m_contraption = contraption;
			LevelManager levelManager = LevelManager;
			BasePart pig = contraption.FindPig();
			m_start = levelManager != null ? levelManager.PigStartPosition : (pig != null ? pig.transform.position : Vector3.zero);
		}

		public override void OnLocalContraptionStopped()
		{
			m_contraption = null;
		}

		public override void Update()
		{
			base.Update();
			if (Phase != RoundPhase.Running || m_contraption == null)
			{
				return;
			}
			BasePart pig = m_contraption.FindPig();
			if (pig == null)
			{
				return;
			}
			Vector3 position = pig.transform.position;
			float value = CurrentMetric == Metric.Height
				? position.y - m_start.y
				: Vector2.Distance(new Vector2(position.x, position.z), new Vector2(m_start.x, m_start.z));
			if (value > m_bestThisRound)
			{
				m_bestThisRound = value;
				ReportScore(m_bestThisRound);
			}
		}

		protected override void DrawModeWindow(MultiplayerUI ui)
		{
			if (!IsHost)
			{
				GUILayout.Label("Measuring: " + (CurrentMetric == Metric.Height ? "height" : "distance"), ui.Label);
				return;
			}
			GUILayout.BeginHorizontal();
			GUILayout.Label("Measure", ui.Label, GUILayout.Width(130f * ui.Scale));
			bool running = Phase == RoundPhase.Running;
			GUI.enabled = !running;
			if (GUILayout.Toggle(CurrentMetric == Metric.Distance, " distance", ui.ToggleStyle) && !running)
			{
				CurrentMetric = Metric.Distance;
			}
			if (GUILayout.Toggle(CurrentMetric == Metric.Height, " height", ui.ToggleStyle) && !running)
			{
				CurrentMetric = Metric.Height;
			}
			GUI.enabled = true;
			GUILayout.EndHorizontal();
		}

		protected override void DrawModeHud(MultiplayerUI ui)
		{
			if (Phase != RoundPhase.Running || m_contraption == null)
			{
				return;
			}
			float width = 260f * ui.Scale;
			float lineHeight = 20f * ui.Scale;
			float x = Screen.width - width - 8f * ui.Scale;
			float y = 8f * ui.Scale + lineHeight * 5f;
			ui.DrawShadowedLabel(new Rect(x, y, width, lineHeight), "Your best: " + FormatScore(m_bestThisRound), ui.HudBold);
		}
	}
}
