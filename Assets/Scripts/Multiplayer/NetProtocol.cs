using System;
using System.IO;
using System.Text;
using UnityEngine;

namespace BPRE.Multiplayer
{
	/// <summary>
	/// Wire protocol shared by host and clients.
	/// Every message is framed as [int32 length][byte type][payload].
	/// </summary>
	public static class NetProtocol
	{
		public const int ProtocolVersion = 1;

		public const int DefaultPort = 7777;

		public const int MaxPlayers = 8;

		public const int MaxMessageSize = 4 * 1024 * 1024;

		public const int MaxNameLength = 16;

		public const int MaxChatLength = 200;

		/// <summary>How often the local contraption state is broadcast (per second).</summary>
		public const float StateSendRate = 15f;

		public const float PingInterval = 3f;

		public const float ConnectionTimeout = 20f;

		public const float ConnectTimeout = 6f;

		public static string SanitizeName(string name)
		{
			if (string.IsNullOrEmpty(name))
			{
				return string.Empty;
			}
			StringBuilder sb = new StringBuilder(name.Length);
			foreach (char c in name)
			{
				if (!char.IsControl(c) && c != '|')
				{
					sb.Append(c);
				}
			}
			string result = sb.ToString().Trim();
			if (result.Length > MaxNameLength)
			{
				result = result.Substring(0, MaxNameLength);
			}
			return result;
		}

		public static string SanitizeChat(string text)
		{
			if (string.IsNullOrEmpty(text))
			{
				return string.Empty;
			}
			StringBuilder sb = new StringBuilder(text.Length);
			foreach (char c in text)
			{
				if (!char.IsControl(c))
				{
					sb.Append(c);
				}
			}
			string result = sb.ToString().Trim();
			if (result.Length > MaxChatLength)
			{
				result = result.Substring(0, MaxChatLength);
			}
			return result;
		}
	}

	public enum NetMessageType : byte
	{
		// Session handshake
		Hello = 1,
		Welcome = 2,
		Reject = 3,
		PlayerJoined = 4,
		PlayerLeft = 5,
		PlayerList = 6,

		// Session traffic
		Chat = 10,
		HostLocation = 11,
		PlayerLocation = 12,

		// Game modes
		ModeSelect = 40,
		ModeMessage = 41,

		// Contraption replication (sent by the owner, relayed by the host)
		ContraptionStart = 20,
		ContraptionState = 21,
		ContraptionStop = 22,

		// Keepalive
		Ping = 30,
		Pong = 31
	}

	/// <summary>Builds one outgoing message. The type byte is written first.</summary>
	public sealed class NetWriter : IDisposable
	{
		private readonly MemoryStream m_stream;

		private readonly BinaryWriter m_writer;

		public NetWriter(NetMessageType type)
		{
			m_stream = new MemoryStream(64);
			m_writer = new BinaryWriter(m_stream, Encoding.UTF8);
			m_writer.Write((byte)type);
		}

		public NetWriter Write(byte value)
		{
			m_writer.Write(value);
			return this;
		}

		public NetWriter Write(bool value)
		{
			m_writer.Write(value);
			return this;
		}

		public NetWriter Write(int value)
		{
			m_writer.Write(value);
			return this;
		}

		public NetWriter Write(float value)
		{
			m_writer.Write(value);
			return this;
		}

		public NetWriter Write(string value)
		{
			m_writer.Write(value ?? string.Empty);
			return this;
		}

		public NetWriter Write(Vector3 value)
		{
			m_writer.Write(value.x);
			m_writer.Write(value.y);
			m_writer.Write(value.z);
			return this;
		}

		public NetWriter Write(Quaternion value)
		{
			m_writer.Write(value.x);
			m_writer.Write(value.y);
			m_writer.Write(value.z);
			m_writer.Write(value.w);
			return this;
		}

		public byte[] ToArray()
		{
			m_writer.Flush();
			return m_stream.ToArray();
		}

		public void Dispose()
		{
			m_writer.Dispose();
			m_stream.Dispose();
		}
	}

	/// <summary>Reads one incoming message. The type byte is consumed by the constructor.</summary>
	public sealed class NetReader : IDisposable
	{
		private readonly MemoryStream m_stream;

		private readonly BinaryReader m_reader;

		public NetMessageType Type { get; }

		public byte[] Raw { get; }

		public NetReader(byte[] data)
		{
			Raw = data;
			m_stream = new MemoryStream(data, writable: false);
			m_reader = new BinaryReader(m_stream, Encoding.UTF8);
			Type = (NetMessageType)m_reader.ReadByte();
		}

		public long Remaining => m_stream.Length - m_stream.Position;

		public byte ReadByte()
		{
			return m_reader.ReadByte();
		}

		public bool ReadBool()
		{
			return m_reader.ReadBoolean();
		}

		public int ReadInt()
		{
			return m_reader.ReadInt32();
		}

		public float ReadFloat()
		{
			return m_reader.ReadSingle();
		}

		public string ReadString()
		{
			return m_reader.ReadString();
		}

		public Vector3 ReadVector3()
		{
			float x = m_reader.ReadSingle();
			float y = m_reader.ReadSingle();
			float z = m_reader.ReadSingle();
			return new Vector3(x, y, z);
		}

		public Quaternion ReadQuaternion()
		{
			float x = m_reader.ReadSingle();
			float y = m_reader.ReadSingle();
			float z = m_reader.ReadSingle();
			float w = m_reader.ReadSingle();
			return new Quaternion(x, y, z, w);
		}

		public void Dispose()
		{
			m_reader.Dispose();
			m_stream.Dispose();
		}
	}
}
