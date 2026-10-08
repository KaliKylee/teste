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

			void Stage(string name, Action action)
			{
				log("[Everest] " + name);
				try
				{
					action();
				}
				catch (Exception e)
				{
					throw new Exception($"[{name}] {Describe(e)}", e);
				}
			}

			Stage("1/4 instalar no Celeste.exe", () => RunPatch(celesteExe, stage1, new[] { everestMm }, deps, log, finalize: false));
			Stage("2/4 gerar MMHOOK", () => GenerateHooks(stage1, mmhook, everestDir, deps, log));
			Stage("3/4 ajustar MMHOOK", () =>
			{
				// As regras do Everest leem os atributos dos tipos do MMHOOK: precisa ler tudo (não adiado), como o Webleste faz.
				RunPatch(mmhook, mmhook + ".tmp", new[] { everestMm }, deps, log, finalize: false, ReadingMode.Immediate);
				File.Move(mmhook + ".tmp", mmhook, overwrite: true);
			});
			Stage("4/4 patches do Android", () => RunPatch(stage1, outputDll, new[] { androidMod }, deps, log, finalize: true));
			File.Delete(stage1);
			log($"Celeste + Everest patcheado: {outputDll}");
		}

		/// <summary>Causa raiz + primeiras linhas da pilha, para o erro mostrado na tela dizer onde falhou.</summary>
		public static string Describe(Exception e)
		{
			Exception inner = e;
			while (inner.InnerException != null)
				inner = inner.InnerException;
			IEnumerable<string> frames = (inner.StackTrace ?? "").Split('\n')
				.Select(l => l.Trim()).Where(l => l.Length > 0).Take(4)
				.Select(l => l.StartsWith("at ") ? l[3..] : l)
				.Select(l => { int paren = l.IndexOf('('); return paren > 0 ? l[..paren] : l; });
			return $"{inner.GetType().Name}: {inner.Message} @ {string.Join(" < ", frames)}";
		}

		private static void RunPatch(string input, string output, IEnumerable<string> mods, IEnumerable<string> dependencyDirs, Action<string> log, bool finalize, ReadingMode readingMode = ReadingMode.Deferred)
		{
			Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);

			using LoggingModder modder = new(log)
			{
				InputPath = input,
				OutputPath = output,
				ReadingMode = readingMode,
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

			const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

			object gen = Activator.CreateInstance(genType, modder, Path.GetFileName(mmhookOutput))!;

			// No MonoMod esses membros são campos públicos; aceita campo ou propriedade.
			void Set(string name, object value)
			{
				FieldInfo? f = genType.GetField(name, Flags);
				if (f != null) { f.SetValue(gen, value); return; }
				genType.GetProperty(name, Flags)?.SetValue(gen, value);
			}
			object? Get(string name) => genType.GetField(name, Flags)?.GetValue(gen) ?? genType.GetProperty(name, Flags)?.GetValue(gen);

			Set("HookPrivate", true);

			MethodInfo generate = genType.GetMethods(Flags).FirstOrDefault(m => m.Name == "Generate" && m.GetParameters().All(p => p.IsOptional))
				?? throw new MissingMethodException("HookGenerator.Generate não encontrado. Membros: "
					+ string.Join(", ", genType.GetMembers(Flags).Select(m => m.Name).Distinct().Take(30)));
			generate.Invoke(gen, generate.GetParameters().Select(p => p.DefaultValue).ToArray());

			var output = Get("OutputModule") as ModuleDefinition
				?? throw new InvalidOperationException("HookGenerator não produziu OutputModule. Membros: "
					+ string.Join(", ", genType.GetMembers(Flags).Select(m => m.Name).Distinct().Take(30)));
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
