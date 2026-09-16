using System;
using System.IO;
using UnityEngine;

namespace BPRE.Updater
{
	/// <summary>
	/// Identity of the running build, written by the release pipeline to
	/// StreamingAssets/bpre_build.json. Development builds have no such file (build 0)
	/// and never update themselves.
	/// </summary>
	[Serializable]
	public sealed class BuildInfo
	{
		public const string FileName = "bpre_build.json";

		/// <summary>Monotonic build number (yyyyMMddHHmm, UTC). 0 = development build.</summary>
		public int build;

		public string version = "dev";

		public string channel = "dev";

		/// <summary>Base URL of the update feed, ending with a slash; contains latest.json.</summary>
		public string updateUrl = string.Empty;

		private static BuildInfo s_current;

		public static BuildInfo Current
		{
			get
			{
				if (s_current == null)
				{
					s_current = Load();
				}
				return s_current;
			}
		}

		public bool IsRelease => build > 0 && !string.IsNullOrEmpty(updateUrl);

		private static BuildInfo Load()
		{
			try
			{
				string path = Path.Combine(Application.streamingAssetsPath, FileName);
				if (File.Exists(path))
				{
					BuildInfo info = JsonUtility.FromJson<BuildInfo>(File.ReadAllText(path));
					if (info != null)
					{
						if (!string.IsNullOrEmpty(info.updateUrl) && !info.updateUrl.EndsWith("/"))
						{
							info.updateUrl += "/";
						}
						return info;
					}
				}
			}
			catch (Exception ex)
			{
				Debug.LogWarning("[Updater] Could not read build info: " + ex.Message);
			}
			return new BuildInfo();
		}
	}
}
