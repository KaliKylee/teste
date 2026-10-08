using System;
using System.IO;
using System.Linq;
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
			main.Invoke(null, args);
		}
	}
}
