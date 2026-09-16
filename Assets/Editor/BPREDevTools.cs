using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Small developer helpers: build a Windows player from the menu, or from outside the
/// editor by dropping a request file into Temp/ (used for automated multiplayer tests).
/// </summary>
public static class BPREDevTools
{
	private const string RequestFile = "Temp/bpre_build_request.txt";

	private const string ResultFile = "Temp/bpre_build_result.txt";

	private const string ExeName = "BadPiggiesRebooted.exe";

	private static double s_nextPoll;

	[InitializeOnLoadMethod]
	private static void Initialize()
	{
		EditorApplication.update += Poll;
	}

	private static void Poll()
	{
		if (EditorApplication.timeSinceStartup < s_nextPoll)
		{
			return;
		}
		s_nextPoll = EditorApplication.timeSinceStartup + 2.0;
		if (!File.Exists(RequestFile) || EditorApplication.isCompiling || EditorApplication.isUpdating)
		{
			return;
		}
		string outputDir = File.ReadAllText(RequestFile).Trim();
		if (EditorApplication.isPlaying)
		{
			// Builds cannot run in play mode; leave the request in place and retry after exiting.
			EditorApplication.isPlaying = false;
			return;
		}
		File.Delete(RequestFile);
		BuildWindowsPlayer(outputDir);
	}

	[MenuItem("BPRE/Build Windows Player")]
	private static void BuildFromMenu()
	{
		BuildWindowsPlayer("Build/Windows");
	}

	/// <summary>
	/// Headless build: Unity.exe -batchmode -quit -projectPath &lt;project&gt;
	///   -executeMethod BPREDevTools.BuildFromCommandLine -buildOutput &lt;dir&gt;
	/// </summary>
	public static void BuildFromCommandLine()
	{
		string outputDir = GetArg("-buildOutput") ?? "Build/Windows";
		BuildWindowsPlayer(outputDir);
		if (!LastBuildSucceeded())
		{
			EditorApplication.Exit(1);
		}
	}

	/// <summary>
	/// Release build for the update feed (called by Tools/Release.ps1):
	///   -executeMethod BPREDevTools.BuildRelease -buildOutput &lt;dir&gt; -buildNumber &lt;n&gt;
	///   -buildVersion &lt;text&gt; -updateUrl &lt;url&gt;
	/// Writes StreamingAssets/bpre_build.json into the player so it can update itself.
	/// </summary>
	public static void BuildRelease()
	{
		string outputDir = GetArg("-buildOutput") ?? "Build/Release";
		int.TryParse(GetArg("-buildNumber") ?? "0", out int buildNumber);
		string version = GetArg("-buildVersion") ?? buildNumber.ToString();
		string updateUrl = GetArg("-updateUrl") ?? string.Empty;
		if (buildNumber <= 0 || string.IsNullOrEmpty(updateUrl))
		{
			Debug.LogError("[BPREDevTools] BuildRelease needs -buildNumber and -updateUrl");
			EditorApplication.Exit(2);
			return;
		}
		BuildWindowsPlayer(outputDir, new BPRE.Updater.BuildInfo
		{
			build = buildNumber,
			version = version,
			channel = "release",
			updateUrl = updateUrl
		});
		if (!LastBuildSucceeded())
		{
			EditorApplication.Exit(1);
		}
	}

	private static string GetArg(string name)
	{
		string[] args = Environment.GetCommandLineArgs();
		for (int i = 0; i < args.Length - 1; i++)
		{
			if (args[i] == name)
			{
				return args[i + 1];
			}
		}
		return null;
	}

	private static bool LastBuildSucceeded()
	{
		return File.Exists(ResultFile) && File.ReadAllText(ResultFile).StartsWith("Succeeded");
	}

	private static void BuildWindowsPlayer(string outputDir, BPRE.Updater.BuildInfo buildInfo = null)
	{
		string result;
		try
		{
			string[] scenes = EditorBuildSettings.scenes.Where(s => s.enabled).Select(s => s.path).ToArray();
			BuildPlayerOptions options = new BuildPlayerOptions
			{
				scenes = scenes,
				locationPathName = Path.Combine(outputDir, ExeName),
				target = BuildTarget.StandaloneWindows64,
				options = BuildOptions.None
			};
			var report = BuildPipeline.BuildPlayer(options);
			result = report.summary.result + " | errors: " + report.summary.totalErrors + " | " + report.summary.totalTime.TotalSeconds.ToString("0") + " s | " + report.summary.outputPath;
			if (report.summary.result == UnityEditor.Build.Reporting.BuildResult.Succeeded)
			{
				// Build identity for the updater. Development builds must never carry a stale file
				// from an earlier release build in the same folder, or they would update themselves.
				string dataDir = Path.Combine(outputDir, Path.GetFileNameWithoutExtension(ExeName) + "_Data", "StreamingAssets");
				string infoPath = Path.Combine(dataDir, BPRE.Updater.BuildInfo.FileName);
				if (buildInfo != null)
				{
					Directory.CreateDirectory(dataDir);
					File.WriteAllText(infoPath, JsonUtility.ToJson(buildInfo, true));
					result += " | release " + buildInfo.version;
				}
				else if (File.Exists(infoPath))
				{
					File.Delete(infoPath);
				}
			}
		}
		catch (Exception ex)
		{
			result = "Exception | " + ex;
		}
		Debug.Log("[BPREDevTools] Build: " + result);
		File.WriteAllText(ResultFile, result);
	}
}
