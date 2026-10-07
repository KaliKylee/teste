using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Mono.Cecil;
using MonoMod;

namespace CelesteAndroid.Patcher
{
	public static class CelestePatcher
	{
		/// <summary>Patch normal (sem Everest).</summary>
		public static void Patch(string celesteExe, string modAssembly, string outputDll, IEnumerable<string> dependencyDirs, Action<string>? log = null)
		{
			log ??= Console.WriteLine;
			string mod = WithoutEverestTypes(modAssembly);
			RunPatch(celesteExe, outputDll, new[] { mod }, dependencyDirs.Append(Path.GetDirectoryName(Path.GetFullPath(modAssembly))!), log, finalize: true);
			log($"Celeste patcheado: {outputDll}");
		}

		/// <summary>
		/// Instala o Everest no Celeste.exe, gera o MMHOOK_Celeste.dll e aplica os patches do Android por cima
		/// (mesma ordem usada pelo Webleste).
		/// </summary>
		public static void PatchEverest(string celesteExe, string everestDir, string androidMod, string outputDll, IEnumerable<string> dependencyDirs, Action<string>? log = null)
		{
			log ??= Console.WriteLine;
			string everestMm = Path.Combine(everestDir, "Celeste.Mod.mm.dll");
			string mmhook = Path.Combine(everestDir, "MMHOOK_Celeste.dll");
			if (!File.Exists(everestMm))
				throw new FileNotFoundException("Celeste.Mod.mm.dll não encontrado na pasta do Everest.", everestMm);

			List<string> deps = dependencyDirs.Append(everestDir).Append(Path.GetDirectoryName(Path.GetFullPath(androidMod))!).ToList();
			string stage1 = outputDll + ".everest.tmp";

			log("[Everest 1/4] Instalando o Everest no Celeste.exe");
			RunPatch(celesteExe, stage1, new[] { everestMm }, deps, log, finalize: false);

			log("[Everest 2/4] Gerando MMHOOK_Celeste.dll");
			GenerateHooks(stage1, mmhook, everestDir, deps, log);

			log("[Everest 3/4] Ajustando MMHOOK_Celeste.dll");
			RunPatch(mmhook, mmhook + ".tmp", new[] { everestMm }, deps, log, finalize: false);
			File.Move(mmhook + ".tmp", mmhook, overwrite: true);

			log("[Everest 4/4] Aplicando patches do Android");
			RunPatch(stage1, outputDll, new[] { androidMod }, deps, log, finalize: true);
			File.Delete(stage1);
			log($"Celeste + Everest patcheado: {outputDll}");
		}

		private static void RunPatch(string input, string output, IEnumerable<string> mods, IEnumerable<string> dependencyDirs, Action<string> log, bool finalize)
		{
			Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);

			using LoggingModder modder = new(log)
			{
				InputPath = input,
				OutputPath = output,
				ReadingMode = ReadingMode.Deferred,
				MissingDependencyThrow = false,
			};
			modder.DependencyDirs.AddRange(dependencyDirs);

			modder.Read();
			foreach (string mod in mods)
				modder.ReadMod(mod);
			modder.MapDependencies();
			modder.AutoPatch();

			if (finalize)
			{
				ModuleDefinition module = modder.Module;
				module.Attributes &= ~(ModuleAttributes.Required32Bit | ModuleAttributes.Preferred32Bit);
				module.Attributes |= ModuleAttributes.ILOnly;
				module.Architecture = TargetArchitecture.I386;
			}

			modder.Write();
		}

		private static void GenerateHooks(string input, string mmhookOutput, string everestDir, IEnumerable<string> dependencyDirs, Action<string> log)
		{
			string hookGenPath = Path.Combine(everestDir, "MonoMod.RuntimeDetour.HookGen.dll");
			if (!File.Exists(hookGenPath))
				throw new FileNotFoundException("MonoMod.RuntimeDetour.HookGen.dll não veio no zip do Everest.", hookGenPath);

			Assembly hookGen = Assembly.LoadFrom(hookGenPath);
			Type genType = hookGen.GetType("MonoMod.RuntimeDetour.HookGen.HookGenerator", throwOnError: true)!;

			using LoggingModder modder = new(log)
			{
				InputPath = input,
				ReadingMode = ReadingMode.Deferred,
				MissingDependencyThrow = false,
			};
			modder.DependencyDirs.AddRange(dependencyDirs);
			modder.Read();
			modder.MapDependencies();

			object gen = Activator.CreateInstance(genType, modder, Path.GetFileName(mmhookOutput))!;
			genType.GetProperty("HookPrivate")?.SetValue(gen, true);
			genType.GetMethod("Generate", Type.EmptyTypes)!.Invoke(gen, null);
			var output = (ModuleDefinition)genType.GetProperty("OutputModule")!.GetValue(gen)!;
			output.Write(mmhookOutput);
		}

		/// <summary>
		/// Os patches de Celeste.Mod.* só fazem sentido com o Everest instalado. Sem ele, copia o módulo marcando
		/// esses tipos com [MonoModIgnore] para o MonoMod não tentar aplicá-los.
		/// </summary>
		private static string WithoutEverestTypes(string modAssembly)
		{
			string dir = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(modAssembly))!, "vanilla");
			Directory.CreateDirectory(dir);
			string output = Path.Combine(dir, Path.GetFileName(modAssembly));

			using ModuleDefinition module = ModuleDefinition.ReadModule(modAssembly, new ReaderParameters(ReadingMode.Deferred));
			MethodReference ignoreCtor = module.ImportReference(typeof(MonoModIgnore).GetConstructor(Type.EmptyTypes)!);
			foreach (TypeDefinition type in module.Types.ToList())
			{
				if (type.Namespace != null && (type.Namespace == "Celeste.Mod" || type.Namespace.StartsWith("Celeste.Mod.")))
				{
					if (!type.CustomAttributes.Any(a => a.AttributeType.Name == nameof(MonoModIgnore)))
						type.CustomAttributes.Add(new CustomAttribute(ignoreCtor));
				}
			}
			module.Write(output);
			return output;
		}

		private sealed class LoggingModder(Action<string> log) : MonoModder
		{
			public override void Log(string text) => log("[MonoMod] " + text);
		}
	}
}
