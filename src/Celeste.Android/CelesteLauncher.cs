using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Reflection;
using System.Runtime.Loader;
using Android.Content;
using Android.Util;

namespace CelesteAndroid
{
	public static class CelesteLauncher
	{
		public static void Run(Context context)
		{
			string gameDir = GameInstaller.GameDir(context);
			Log.Info(GameActivity.LogTag, $"Iniciando Celeste de {gameDir}");

			AppContext.SetData("CelesteAndroid.Platform", "Android");
			AppContext.SetData("CelesteAndroid.PrefPath", GameInstaller.UserDir(context));
			AppContext.SetData("CelesteAndroid.BackgroundPath", GameInstaller.BackgroundPng(context));
			AppContext.SetData("CelesteAndroid.TouchLayoutPath", GameInstaller.TouchLayoutFile(context));

			AppContext.SetData("APP_CONTEXT_BASE_DIRECTORY", gameDir + Path.DirectorySeparatorChar);
			Environment.CurrentDirectory = gameDir;

			AppContext.SetData("CelesteAndroid.GameDir", gameDir);

			string dll = GameInstaller.PatchedDll(context);
			bool everest = AppContext.GetData("CelesteAndroid.UseEverest") as string == "1" && EverestInstaller.IsInstalled(context);
			if (everest)
			{
				dll = EverestInstaller.PatchedDll(context);
				string everestDir = EverestInstaller.EverestDir(context);
				Log.Info(GameActivity.LogTag, "Everest ativado: " + dll);
				InstallDiagnostics(gameDir);
				PatchMonoModCoreForAndroid(everestDir);
				ForceLinuxPlatform();
				AssemblyLoadContext.Default.Resolving += (ctx, name) =>
				{
					try
					{
						if (name.Name == "Celeste")
							return ctx.LoadFromAssemblyPath(dll);
						string candidate = Path.Combine(everestDir, name.Name + ".dll");
						return File.Exists(candidate) ? ctx.LoadFromAssemblyPath(candidate) : null;
					}
					catch (Exception e)
					{
						Log.Warn(GameActivity.LogTag, $"Falha ao resolver {name.Name}: {e.Message}");
						return null;
					}
				};
			}

			Assembly celeste = AssemblyLoadContext.Default.LoadFromAssemblyPath(dll);

			// O Everest muda a visibilidade de vários membros (Main vira público, por exemplo): procura em qualquer visibilidade.
			const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;

			Type engine = celeste.GetType("Monocle.Engine", throwOnError: true)!;
			FieldInfo? assemblyDirField = engine.GetField("AssemblyDirectory", Any);
			if (assemblyDirField != null)
			{
				assemblyDirField.SetValue(null, gameDir);
			}
			else
			{
				PropertyInfo? assemblyDirProp = engine.GetProperty("AssemblyDirectory", Any);
				if (assemblyDirProp?.CanWrite == true)
					assemblyDirProp.SetValue(null, gameDir);
				else
					Log.Warn(GameActivity.LogTag, "Monocle.Engine.AssemblyDirectory não encontrado; seguindo sem definir.");
			}

			Type celesteType = celeste.GetType("Celeste.Celeste", throwOnError: true)!;
			MethodInfo main = celesteType.GetMethod("Main", Any)
				?? throw new MissingMethodException("Celeste.Celeste.Main não encontrado. Métodos estáticos: "
					+ string.Join(", ", celesteType.GetMethods(Any).Select(m => m.Name).Distinct().Take(30)));

			Log.Info(GameActivity.LogTag, $"Chamando {main.DeclaringType}.{main.Name} ({main.GetParameters().Length} parâmetro(s))");
			object?[]? args = main.GetParameters().Length == 0 ? null : new object?[] { Array.Empty<string>() };
			try
			{
				main.Invoke(null, args);
				Log.Warn(GameActivity.LogTag, "Celeste.Main retornou (o jogo encerrou sozinho).");
			}
			catch (TargetInvocationException e) when (e.InnerException != null)
			{
				Log.Error(GameActivity.LogTag, "Celeste.Main lançou exceção: " + e.InnerException);
				throw;
			}
			finally
			{
				if (everest)
					DumpEverestLogs(gameDir);
			}
		}

		/// <summary>
		/// O MonoMod.Core detecta o Android (existem /data e /system/build.prop) e não tem sistema para ele
		/// (NotImplementedException em PlatformTriple.CreateCurrentSystem). Como o Android é Linux por baixo,
		/// troca o valor detectado para Linux antes de qualquer detour ser criado.
		/// </summary>
		// A libc do Android (bionic) não tem __errno_location (só __errno). O MonoMod.Core importa o primeiro,
		// então troca o nome no metadado da DLL (mesmo tamanho de campo, terminado em zero) e carrega essa cópia antes de tudo.
		private static void PatchMonoModCoreForAndroid(string everestDir)
		{
			try
			{
				string path = Path.Combine(everestDir, "MonoMod.Core.dll");
				if (!File.Exists(path))
				{
					Log.Warn(GameActivity.LogTag, "MonoMod.Core.dll não encontrado em " + everestDir);
					return;
				}

				byte[] data = File.ReadAllBytes(path);
				byte[] from = Encoding.ASCII.GetBytes("__errno_location\0");
				byte[] to = new byte[from.Length];
				Encoding.ASCII.GetBytes("__errno").CopyTo(to, 0);

				int count = 0;
				for (int i = 0; i <= data.Length - from.Length; i++)
				{
					bool match = true;
					for (int j = 0; j < from.Length; j++)
					{
						if (data[i + j] != from[j]) { match = false; break; }
					}
					if (!match) continue;
					Array.Copy(to, 0, data, i, to.Length);
					count++;
					i += from.Length - 1;
				}
				if (count > 0)
				{
					File.WriteAllBytes(path, data);
					Log.Info(GameActivity.LogTag, $"MonoMod.Core: __errno_location -> __errno ({count}x).");
				}

				string name = "MonoMod.Core";
				Assembly? loaded = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == name);
				if (loaded != null)
					Log.Warn(GameActivity.LogTag, "MonoMod.Core já estava carregado (" + loaded.Location + "); a cópia corrigida pode não ser usada.");
				else
					AssemblyLoadContext.Default.LoadFromAssemblyPath(path);
			}
			catch (Exception e)
			{
				Log.Error(GameActivity.LogTag, "Falha ao ajustar MonoMod.Core: " + e);
			}
		}

		private static void ForceLinuxPlatform()
		{
			try
			{
				Assembly utils = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "MonoMod.Utils")
					?? Assembly.Load(new AssemblyName("MonoMod.Utils"));
				Type detection = utils.GetType("MonoMod.Utils.PlatformDetection", throwOnError: true)!;

				object? detected = detection.GetProperty("OS", BindingFlags.Public | BindingFlags.Static)?.GetValue(null); // força a detecção
				FieldInfo? os = detection.GetField("os", BindingFlags.NonPublic | BindingFlags.Static);
				Log.Info(GameActivity.LogTag, $"MonoMod detectou o SO como: {detected}");
				if (os == null)
				{
					Log.Warn(GameActivity.LogTag, "PlatformDetection.os não encontrado; membros: "
						+ string.Join(", ", detection.GetFields(BindingFlags.NonPublic | BindingFlags.Static).Select(f => f.Name)));
					return;
				}
				if (os.GetValue(null)?.ToString() == "Android")
				{
					os.SetValue(null, Enum.Parse(os.FieldType, "Linux"));
					Log.Info(GameActivity.LogTag, "MonoMod: SO trocado de Android para Linux.");
				}
			}
			catch (Exception e)
			{
				Log.Error(GameActivity.LogTag, "Falha ao ajustar a detecção de plataforma do MonoMod: " + e);
			}
		}

		private static void InstallDiagnostics(string gameDir)
		{
			// O Everest escreve no Console; sem isso nada disso chega ao logcat.
			Console.SetOut(new LogcatWriter("[out] "));
			Console.SetError(new LogcatWriter("[err] "));

			AppDomain.CurrentDomain.UnhandledException += (_, e) =>
				Log.Error(GameActivity.LogTag, "UnhandledException: " + e.ExceptionObject);
			TaskScheduler.UnobservedTaskException += (_, e) =>
				Log.Error(GameActivity.LogTag, "UnobservedTaskException: " + e.Exception);
			AppDomain.CurrentDomain.ProcessExit += (_, _) =>
				Log.Warn(GameActivity.LogTag, "ProcessExit chamado. Pilha: " + Environment.StackTrace);
		}

		private static void DumpEverestLogs(string gameDir)
		{
			string[] candidates =
			{
				Path.Combine(gameDir, "log.txt"),
				Path.Combine(gameDir, "Everest", "log.txt"),
				Path.Combine(gameDir, "error_log.txt"),
				Path.Combine(gameDir, "Logs", "log.txt"),
			};
			foreach (string file in candidates)
			{
				try
				{
					if (!File.Exists(file))
						continue;
					string[] lines = File.ReadAllLines(file);
					Log.Info(GameActivity.LogTag, $"--- {file} (últimas {Math.Min(60, lines.Length)} linhas) ---");
					foreach (string line in lines.Skip(Math.Max(0, lines.Length - 60)))
						Log.Info(GameActivity.LogTag, line);
				}
				catch (Exception e)
				{
					Log.Warn(GameActivity.LogTag, $"Não consegui ler {file}: {e.Message}");
				}
			}
		}

		private sealed class LogcatWriter(string prefix) : TextWriter
		{
			private readonly StringBuilder line = new();

			public override Encoding Encoding => Encoding.UTF8;

			public override void Write(char value)
			{
				lock (line)
				{
					if (value == '\n')
					{
						Log.Info(GameActivity.LogTag, prefix + line);
						line.Clear();
					}
					else if (value != '\r')
					{
						line.Append(value);
					}
				}
			}

			public override void Write(string? value)
			{
				if (value == null)
					return;
				foreach (char c in value)
					Write(c);
			}

			public override void Flush()
			{
				lock (line)
				{
					if (line.Length > 0)
					{
						Log.Info(GameActivity.LogTag, prefix + line);
						line.Clear();
					}
				}
			}
		}
	}
}
