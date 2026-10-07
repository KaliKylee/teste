using System;
using System.IO;

// Só aplicado quando o Everest está instalado (sem ele, o patcher marca Celeste.Mod.* como [MonoModIgnore]).
namespace Celeste.Mod
{
	public static partial class patch_Everest
	{
		private static string AndroidGameDir => AppContext.GetData("CelesteAndroid.GameDir") as string ?? "/";

		public static string PathGame
		{
			get => AndroidGameDir;
			set { }
		}

		public static class patch_Loader
		{
			public static string PathMods
			{
				get => Path.Combine(AppContext.GetData("CelesteAndroid.GameDir") as string ?? "/", "Mods");
				set { }
			}
		}
	}
}
