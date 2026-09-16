using System;
using System.Collections.Generic;
using UnityEngine;

namespace BPRE.Multiplayer
{
	/// <summary>
	/// Shared round and scoreboard handling for competitive modes. The host starts
	/// and ends rounds; players report their own score, the host keeps the table
	/// and broadcasts it.
	/// </summary>
	public abstract class RoundBasedMode : MultiplayerGameMode
	{
		protected const byte MsgRoundStart = 1;

		protected const byte MsgRoundEnd = 2;

		protected const byte MsgScore = 3;

		protected const byte MsgScoreboard = 4;

		private const float ScoreboardInterval = 0.5f;

		private const float ScoreReportInterval = 0.25f;

		public enum RoundPhase
		{
			Idle,
			Running,
			Finished
		}

		public struct ScoreEntry
		{
			public int PlayerId;

			public string Name;

			public float Score;
		}

		private readonly Dictionary<int, float> m_scores = new Dictionary<int, float>();

		private readonly List<ScoreEntry> m_ranking = new List<ScoreEntry>();

		private bool m_scoreboardDirty;

		private float m_lastScoreboardSent;

		private float m_pendingLocalScore;

		private bool m_hasPendingLocalScore;

		private float m_lastScoreReport;

		private string m_durationField;

		public RoundPhase Phase { get; private set; }

		public int RoundNumber { get; private set; }

		/// <summary>Round length in seconds (host setting).</summary>
		public float RoundDuration { get; set; } = 120f;

		public float RoundEndsAt { get; private set; }

		public float TimeLeft => Phase == RoundPhase.Running ? Mathf.Max(0f, RoundEndsAt - Time.realtimeSinceStartup) : 0f;

		public IReadOnlyList<ScoreEntry> Ranking => m_ranking;

		protected virtual bool HigherIsBetter => true;

		/// <summary>Whether rounds are timed; untimed rounds end manually or when the mode decides.</summary>
		protected virtual bool TimedRounds => true;

		protected RoundBasedMode(MultiplayerSession session)
			: base(session)
		{
		}

		// ------------------------------------------------------------------
		// Host controls
		// ------------------------------------------------------------------

		public void StartRound()
		{
			if (!IsHost || Phase == RoundPhase.Running)
			{
				return;
			}
			int seed = UnityEngine.Random.Range(1, int.MaxValue);
			int roundNumber = RoundNumber + 1;
			float duration = RoundDuration;
			// Announce the round before running the local start: modes send their initial state
			// (e.g. the flag position) from OnRoundStarted, and clients must receive that after RoundStart.
			Send(MsgRoundStart, w =>
			{
				w.Write(roundNumber).Write(duration).Write(seed);
				WriteRoundStart(w);
			});
			BeginRoundLocally(roundNumber, duration, seed);
		}

		public void EndRound()
		{
			if (!IsHost || Phase != RoundPhase.Running)
			{
				return;
			}
			RebuildRanking();
			Send(MsgRoundEnd, w => WriteScoreTable(w));
			FinishRoundLocally();
		}

		private void BeginRoundLocally(int roundNumber, float duration, int seed)
		{
			RoundNumber = roundNumber;
			RoundDuration = duration;
			Phase = RoundPhase.Running;
			RoundEndsAt = Time.realtimeSinceStartup + duration;
			m_scores.Clear();
			m_ranking.Clear();
			m_hasPendingLocalScore = false;
			Announce("Round " + roundNumber + " started" + (TimedRounds ? " (" + Mathf.RoundToInt(duration) + " s)." : "."));
			OnRoundStarted(seed);
		}

		private void FinishRoundLocally()
		{
			Phase = RoundPhase.Finished;
			if (m_ranking.Count > 0)
			{
				ScoreEntry winner = m_ranking[0];
				Announce("Round " + RoundNumber + " over. Winner: " + winner.Name + " with " + FormatScore(winner.Score) + ".");
			}
			else
			{
				Announce("Round " + RoundNumber + " over.");
			}
			OnRoundEnded();
		}

		// ------------------------------------------------------------------
		// Scores
		// ------------------------------------------------------------------

		public float GetScore(int playerId)
		{
			return m_scores.TryGetValue(playerId, out float score) ? score : 0f;
		}

		/// <summary>Reports the local player's current score; only improvements are kept.</summary>
		protected void ReportScore(float score)
		{
			if (Phase != RoundPhase.Running || LocalPlayer == null)
			{
				return;
			}
			if (!AcceptScore(GetScore(LocalPlayer.Id), score))
			{
				return;
			}
			m_scores[LocalPlayer.Id] = score;
			m_pendingLocalScore = score;
			m_hasPendingLocalScore = true;
			if (IsHost)
			{
				m_scoreboardDirty = true;
			}
		}

		/// <summary>Host only: adds points to any player (used for events the host validates).</summary>
		protected void AwardPoints(MultiplayerPlayer player, float points)
		{
			if (!IsHost || player == null || Phase != RoundPhase.Running)
			{
				return;
			}
			m_scores[player.Id] = GetScore(player.Id) + points;
			m_scoreboardDirty = true;
		}

		protected virtual bool AcceptScore(float current, float candidate)
		{
			return HigherIsBetter ? candidate > current : (candidate < current || current == 0f);
		}

		protected virtual string FormatScore(float score)
		{
			return score.ToString("0");
		}

		private void RebuildRanking()
		{
			m_ranking.Clear();
			foreach (KeyValuePair<int, float> pair in m_scores)
			{
				MultiplayerPlayer player = Session.GetPlayer(pair.Key);
				m_ranking.Add(new ScoreEntry
				{
					PlayerId = pair.Key,
					Name = player != null ? player.Name : "#" + pair.Key,
					Score = pair.Value
				});
			}
			// List everyone in the session, so a player without a result yet is visible as 0.
			if (Phase != RoundPhase.Idle)
			{
				foreach (MultiplayerPlayer player in Session.Players)
				{
					if (!m_scores.ContainsKey(player.Id))
					{
						m_ranking.Add(new ScoreEntry { PlayerId = player.Id, Name = player.Name, Score = 0f });
					}
				}
			}
			m_ranking.Sort((a, b) => HigherIsBetter ? b.Score.CompareTo(a.Score) : a.Score.CompareTo(b.Score));
		}

		private void WriteScoreTable(NetWriter w)
		{
			w.Write(m_scores.Count);
			foreach (KeyValuePair<int, float> pair in m_scores)
			{
				w.Write(pair.Key).Write(pair.Value);
			}
		}

		private void ReadScoreTable(NetReader r)
		{
			int count = r.ReadInt();
			m_scores.Clear();
			for (int i = 0; i < count; i++)
			{
				int id = r.ReadInt();
				float score = r.ReadFloat();
				m_scores[id] = score;
			}
			RebuildRanking();
		}

		// ------------------------------------------------------------------
		// Hooks for concrete modes
		// ------------------------------------------------------------------

		protected virtual void OnRoundStarted(int seed)
		{
		}

		protected virtual void OnRoundEnded()
		{
		}

		/// <summary>Host: append mode specific round parameters to the round start message.</summary>
		protected virtual void WriteRoundStart(NetWriter w)
		{
		}

		/// <summary>Client: read what <see cref="WriteRoundStart"/> wrote.</summary>
		protected virtual void ReadRoundStart(NetReader r)
		{
		}

		protected virtual void HandleModeMessage(MultiplayerPlayer from, byte subType, NetReader reader)
		{
		}

		/// <summary>Mode specific controls and information below the round panel.</summary>
		protected virtual void DrawModeWindow(MultiplayerUI ui)
		{
		}

		protected virtual void DrawModeHud(MultiplayerUI ui)
		{
		}

		// ------------------------------------------------------------------
		// Base behaviour
		// ------------------------------------------------------------------

		public override void Update()
		{
			float now = Time.realtimeSinceStartup;
			if (m_hasPendingLocalScore && now - m_lastScoreReport >= ScoreReportInterval)
			{
				m_hasPendingLocalScore = false;
				m_lastScoreReport = now;
				if (!IsHost)
				{
					float score = m_pendingLocalScore;
					Send(MsgScore, w => w.Write(score));
				}
			}
			if (!IsHost)
			{
				return;
			}
			if (Phase == RoundPhase.Running && TimedRounds && now >= RoundEndsAt)
			{
				EndRound();
				return;
			}
			if (m_scoreboardDirty && now - m_lastScoreboardSent >= ScoreboardInterval)
			{
				m_scoreboardDirty = false;
				m_lastScoreboardSent = now;
				RebuildRanking();
				Send(MsgScoreboard, w => WriteScoreTable(w));
			}
		}

		public override void OnPlayerJoined(MultiplayerPlayer player)
		{
			if (!IsHost)
			{
				return;
			}
			// Bring the newcomer up to date with the current round.
			if (Phase == RoundPhase.Running)
			{
				int roundNumber = RoundNumber;
				float remaining = TimeLeft;
				SendTo(player, MsgRoundStart, w =>
				{
					w.Write(roundNumber).Write(remaining).Write(0);
					WriteRoundStart(w);
				});
			}
			RebuildRanking();
			SendTo(player, MsgScoreboard, w => WriteScoreTable(w));
		}

		public override void OnPlayerLeft(MultiplayerPlayer player)
		{
			if (m_scores.Remove(player.Id))
			{
				RebuildRanking();
				m_scoreboardDirty = true;
			}
		}

		public override void OnModeMessage(MultiplayerPlayer from, byte subType, NetReader reader)
		{
			switch (subType)
			{
			case MsgRoundStart:
				if (from.IsHost)
				{
					int roundNumber = reader.ReadInt();
					float duration = reader.ReadFloat();
					int seed = reader.ReadInt();
					ReadRoundStart(reader);
					BeginRoundLocally(roundNumber, duration, seed);
				}
				break;
			case MsgRoundEnd:
				if (from.IsHost && Phase == RoundPhase.Running)
				{
					ReadScoreTable(reader);
					FinishRoundLocally();
				}
				break;
			case MsgScore:
				if (IsHost && Phase == RoundPhase.Running)
				{
					float score = reader.ReadFloat();
					if (AcceptScore(GetScore(from.Id), score))
					{
						m_scores[from.Id] = score;
						m_scoreboardDirty = true;
					}
				}
				break;
			case MsgScoreboard:
				if (from.IsHost)
				{
					ReadScoreTable(reader);
				}
				break;
			default:
				HandleModeMessage(from, subType, reader);
				break;
			}
		}

		public override void OnExit()
		{
			Phase = RoundPhase.Idle;
			m_scores.Clear();
			m_ranking.Clear();
		}

		// ------------------------------------------------------------------
		// GUI
		// ------------------------------------------------------------------

		public override void DrawWindow(MultiplayerUI ui)
		{
			GUILayout.Label(Description, ui.Label);
			string phaseText;
			switch (Phase)
			{
			case RoundPhase.Running:
				phaseText = "Round " + RoundNumber + " running" + (TimedRounds ? " - " + FormatTime(TimeLeft) + " left" : string.Empty);
				break;
			case RoundPhase.Finished:
				phaseText = "Round " + RoundNumber + " finished";
				break;
			default:
				phaseText = "No round running";
				break;
			}
			GUILayout.Label(phaseText, ui.Bold);
			if (IsHost)
			{
				GUILayout.BeginHorizontal();
				if (TimedRounds)
				{
					GUILayout.Label("Round length (s)", ui.Label, GUILayout.Width(130f * ui.Scale));
					if (m_durationField == null)
					{
						m_durationField = Mathf.RoundToInt(RoundDuration).ToString();
					}
					m_durationField = GUILayout.TextField(m_durationField, 4, ui.TextField, GUILayout.Width(60f * ui.Scale));
					if (int.TryParse(m_durationField, out int seconds) && seconds > 0)
					{
						RoundDuration = seconds;
					}
				}
				GUILayout.FlexibleSpace();
				if (Phase == RoundPhase.Running)
				{
					if (GUILayout.Button("End round", ui.ButtonStyle, GUILayout.Width(120f * ui.Scale)))
					{
						EndRound();
					}
				}
				else if (GUILayout.Button("Start round", ui.ButtonStyle, GUILayout.Width(120f * ui.Scale)))
				{
					StartRound();
				}
				GUILayout.EndHorizontal();
			}
			DrawModeWindow(ui);
			if (m_ranking.Count > 0)
			{
				GUILayout.Label("Scoreboard", ui.Bold);
				for (int i = 0; i < m_ranking.Count; i++)
				{
					ScoreEntry entry = m_ranking[i];
					GUILayout.Label((i + 1) + ". " + entry.Name + "  -  " + FormatScore(entry.Score), ui.Label);
				}
			}
		}

		public override void DrawHud(MultiplayerUI ui)
		{
			if (!LocalInLevel)
			{
				return;
			}
			float width = 260f * ui.Scale;
			float lineHeight = 20f * ui.Scale;
			float x = Screen.width - width - 8f * ui.Scale;
			float y = 8f * ui.Scale;
			string header = DisplayName;
			if (Phase == RoundPhase.Running)
			{
				header += TimedRounds ? "  " + FormatTime(TimeLeft) : "  round " + RoundNumber;
			}
			else if (Phase == RoundPhase.Finished)
			{
				header += "  round over";
			}
			else
			{
				header += IsHost ? "  (start a round: F9)" : "  (waiting for host)";
			}
			ui.DrawShadowedLabel(new Rect(x, y, width, lineHeight), header, ui.HudBold);
			y += lineHeight;
			int shown = 0;
			for (int i = 0; i < m_ranking.Count && shown < 4; i++, shown++)
			{
				ScoreEntry entry = m_ranking[i];
				ui.DrawShadowedLabel(new Rect(x, y, width, lineHeight), (i + 1) + ". " + entry.Name + "  " + FormatScore(entry.Score), ui.Hud);
				y += lineHeight;
			}
			DrawModeHud(ui);
		}

		protected static string FormatTime(float seconds)
		{
			int total = Mathf.CeilToInt(seconds);
			return (total / 60) + ":" + (total % 60).ToString("00");
		}
	}
}
