using System;
using System.Security.Cryptography;
using System.Text;

namespace BPRE.Updater
{
	/// <summary>latest.json on the update server, produced by Tools/Release.ps1.</summary>
	[Serializable]
	public sealed class UpdateManifest
	{
		public int build;

		public string version;

		/// <summary>Zip file name, relative to the update URL.</summary>
		public string file;

		/// <summary>Lower-case hex SHA-256 of the zip.</summary>
		public string sha256;

		public long size;

		public string notes;

		public string published;

		/// <summary>Base64 RSA-SHA256 signature over <see cref="SignedPayload"/>.</summary>
		public string signature;

		/// <summary>Exactly the text the release script signs. Must stay in sync with Tools/Release.ps1.</summary>
		public string SignedPayload => build + "|" + version + "|" + file + "|" + (sha256 ?? string.Empty).ToLowerInvariant() + "|" + size;

		public bool IsWellFormed(out string reason)
		{
			if (build <= 0)
			{
				reason = "invalid build number";
				return false;
			}
			if (string.IsNullOrEmpty(file) || file.Contains("/") || file.Contains("\\") || file.Contains(".."))
			{
				reason = "invalid file name";
				return false;
			}
			if (string.IsNullOrEmpty(sha256) || sha256.Length != 64)
			{
				reason = "invalid checksum";
				return false;
			}
			if (size <= 0 || size > 2L * 1024 * 1024 * 1024)
			{
				reason = "invalid size";
				return false;
			}
			if (string.IsNullOrEmpty(signature))
			{
				reason = "not signed";
				return false;
			}
			reason = null;
			return true;
		}

		public bool HasValidSignature()
		{
			try
			{
				byte[] data = Encoding.UTF8.GetBytes(SignedPayload);
				byte[] sig = Convert.FromBase64String(signature);
				using (RSACryptoServiceProvider rsa = new RSACryptoServiceProvider())
				{
					rsa.PersistKeyInCsp = false;
					rsa.FromXmlString(UpdateSigningKey.PublicKeyXml);
					using (SHA256 sha = SHA256.Create())
					{
						return rsa.VerifyData(data, sha, sig);
					}
				}
			}
			catch
			{
				return false;
			}
		}
	}
}
