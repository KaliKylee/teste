using System;
using System.IO;
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

			celeste.GetType("Monocle.Engine", throwOnError: true)!
				.GetField("AssemblyDirectory", BindingFlags.NonPublic | BindingFlags.Static)!
				.SetValue(null, gameDir);

			MethodInfo main = celeste.GetType("Celeste.Celeste", throwOnError: true)!
				.GetMethod("Main", BindingFlags.NonPublic | BindingFlags.Static)!;
			main.Invoke(null, new object[] { Array.Empty<string>() });
		}
	}
}
