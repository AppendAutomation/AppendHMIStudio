using System.Net;

namespace Hmi.Comms.Server;

/// <summary>
/// Command line options. Parsed by hand: the set is small and a dependency
/// would outweigh it.
/// </summary>
internal sealed class ServerOptions
{
	public IPAddress Host { get; set; } = IPAddress.Loopback;
	public int Port { get; set; }
	public string? Token { get; set; }
	public bool TokenFromStdin { get; set; }
	public bool AllowShutdown { get; set; }
	public int? ParentPid { get; set; }
	public string? ConfigPath { get; set; }
	public string ConfigMode { get; set; } = "client";
	public List<string> AllowedOrigins { get; } = new();
	public string LogLevel { get; set; } = "info";
	public string? LogFile { get; set; }
	public int CoalesceMs { get; set; } = 20;

	public const string Usage =
		"hmi-comms --listen <host:port> (--token <t> | --token-stdin) [--allow-shutdown]\n" +
		"          [--parent-pid <pid>] [--config <file> [--config-mode client|file]]\n" +
		"          [--allow-origin <origin>]... [--log-level debug|info|warn|error]\n" +
		"          [--log-file <path>] [--coalesce-ms <n>]";

	public static ServerOptions Parse(string[] args)
	{
		var o = new ServerOptions();

		for (int i = 0; i < args.Length; i++)
		{
			string a = args[i];

			string Next()
			{
				if (i + 1 >= args.Length)
				{
					throw new ArgumentException($"{a} needs a value");
				}

				return args[++i];
			}

			switch (a)
			{
				case "--listen":
					ParseListen(Next(), o);
					break;
				case "--token":
					o.Token = Next();
					break;
				case "--token-stdin":
					o.TokenFromStdin = true;
					break;
				case "--allow-shutdown":
					o.AllowShutdown = true;
					break;
				case "--parent-pid":
					o.ParentPid = int.Parse(Next());
					break;
				case "--config":
					o.ConfigPath = Next();
					break;
				case "--config-mode":
					o.ConfigMode = Next();

					if (o.ConfigMode != "client" && o.ConfigMode != "file")
					{
						throw new ArgumentException("--config-mode must be client or file");
					}

					break;
				case "--allow-origin":
					o.AllowedOrigins.Add(Next());
					break;
				case "--log-level":
					o.LogLevel = Next();
					break;
				case "--log-file":
					o.LogFile = Next();
					break;
				case "--coalesce-ms":
					o.CoalesceMs = Math.Clamp(int.Parse(Next()), 0, 1000);
					break;
				case "--help":
				case "-h":
					throw new HelpRequestedException();
				default:
					throw new ArgumentException($"Unknown option {a}");
			}
		}

		if (o.Token != null && o.TokenFromStdin)
		{
			throw new ArgumentException("Use either --token or --token-stdin, not both");
		}

		return o;
	}

	private static void ParseListen(string value, ServerOptions o)
	{
		int colon = value.LastIndexOf(':');

		if (colon <= 0)
		{
			throw new ArgumentException("--listen must be host:port");
		}

		string host = value.Substring(0, colon);

		o.Host = host switch
		{
			"localhost" => IPAddress.Loopback,
			"*" => IPAddress.Any,
			_ => IPAddress.Parse(host)
		};

		o.Port = int.Parse(value.Substring(colon + 1));

		if (o.Port < 0 || o.Port > 65535)
		{
			throw new ArgumentException("--listen port out of range");
		}
	}
}

internal sealed class HelpRequestedException : Exception
{
}
