using System;
using System.IO;
using System.Linq;
using System.Text;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace CelesteAndroid.Patcher
{
	/// <summary>
	/// Ajusta o MonoMod.Core.dll para rodar na libc do Android (bionic), que difere da glibc:
	///  - não existe __errno_location (só __errno);
	///  - os números do sysconf são outros (_SC_PAGESIZE = 39, na glibc é 30);
	///  - não existe /tmp (o helper nativo é extraído lá), então usa o diretório atual.
	/// </summary>
	public static class MonoModCoreFix
	{
		private const int GlibcPageSize = 30;
		private const int BionicPageSize = 39;
		private const string TempTemplate = "/tmp/mm-exhelper.so.XXXXXX";
		private const string RelativeTemplate = ".////mm-exhelper.so.XXXXXX"; // mesmo tamanho, relativo ao diretório atual

		public static void Apply(string dllPath, Action<string> log)
		{
			byte[] data = File.ReadAllBytes(dllPath);
			bool changed = false;

			using (var input = new MemoryStream(data))
			{
				ModuleDefinition module = ModuleDefinition.ReadModule(input, new ReaderParameters(ReadingMode.Immediate));
				int errno = 0, sysconf = 0;

				foreach (TypeDefinition type in module.GetTypes())
				{
					foreach (MethodDefinition method in type.Methods)
					{
						if (method.HasPInvokeInfo && method.PInvokeInfo.EntryPoint == "__errno_location")
						{
							method.PInvokeInfo.EntryPoint = "__errno";
							errno++;
						}

						if (!method.HasBody)
							continue;

						foreach (Instruction call in method.Body.Instructions)
						{
							if (call.OpCode != OpCodes.Call || call.Operand is not MethodReference target)
								continue;
							if (target.Name != "Sysconf" || target.DeclaringType.Name != "Unix")
								continue;

							Instruction? arg = call.Previous;
							if (arg == null)
								continue;
							if (arg.OpCode == OpCodes.Ldc_I4_S && arg.Operand is sbyte b && b == GlibcPageSize)
							{
								arg.Operand = (sbyte)BionicPageSize;
								sysconf++;
							}
							else if (arg.OpCode == OpCodes.Ldc_I4 && arg.Operand is int i && i == GlibcPageSize)
							{
								arg.Operand = BionicPageSize;
								sysconf++;
							}
						}
					}
				}

				if (errno > 0 || sysconf > 0)
				{
					using var output = new MemoryStream();
					module.Write(output);
					data = output.ToArray();
					changed = true;
					log($"MonoMod.Core: __errno_location -> __errno ({errno}x), sysconf(PageSize) 30 -> {BionicPageSize} ({sysconf}x).");
				}
			}

			// O modelo do arquivo temporário é um literal UTF-8 nos dados da DLL.
			byte[] from = Encoding.ASCII.GetBytes(TempTemplate);
			byte[] to = Encoding.ASCII.GetBytes(RelativeTemplate);
			int templates = 0;
			for (int i = 0; i <= data.Length - from.Length; i++)
			{
				int j = 0;
				while (j < from.Length && data[i + j] == from[j]) j++;
				if (j != from.Length) continue;
				Array.Copy(to, 0, data, i, to.Length);
				templates++;
				i += from.Length - 1;
			}
			if (templates > 0)
			{
				changed = true;
				log($"MonoMod.Core: /tmp -> diretório atual ({templates}x).");
			}

			if (changed)
				File.WriteAllBytes(dllPath, data);
			else
				log("MonoMod.Core: nada a ajustar (já corrigido).");
		}
	}
}
