using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using Android.Content;

namespace CelesteAndroid
{
	public static class EverestInstaller
	{
		private const string UpdaterUrl = "https://everestapi.github.io/everestupdater.txt";
		private const string Branch = "stable";

		public static string EverestDir(Context context) => Path.Combine(GameInstaller.GameDir(context), "Everest");

		public static string PatchedDll(Context context) =>
			Path.Combine(Path.GetDirectoryName(GameInstaller.PatchedDll(context))!, "CelesteEverest.dll");

		public static string LogFile(Context context) => Path.Combine(context.FilesDir!.AbsolutePath, "everest-install.log");

		public static string ModsDir(Context context) => Path.Combine(GameInstaller.GameDir(context), "Mods");

		public static bool IsInstalled(Context context) =>
			File.Exists(PatchedDll(context)) && File.Exists(Path.Combine(EverestDir(context), "Celeste.Mod.mm.dll"));

		/// <summary>Baixa o build estável do Everest e extrai em Celeste/Everest. Retorna a pasta.</summary>
		public static string DownloadAndExtract(Context context, Action<string, float> progress, Action<string> log)
		{
			using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(15) };
			http.DefaultRequestHeaders.UserAgent.ParseAdd("CelesteAndroid");

			string versionsUrl = http.GetStringAsync(UpdaterUrl).GetAwaiter().GetResult().Trim();
			string json = http.GetStringAsync(versionsUrl + "?supportsNativeBuilds=true").GetAwaiter().GetResult();

			using JsonDocument doc = JsonDocument.Parse(json);
			JsonElement? build = null;
			foreach (JsonElement el in doc.RootElement.EnumerateArray())
			{
				if (el.TryGetProperty("branch", out var b) && b.GetString() == Branch && el.TryGetProperty("mainDownload", out _))
				{
					build = el;
					break;
				}
			}
			if (build is not JsonElement chosen)
				throw new InstallException("Nenhum build do Everest encontrado.");

			string url = chosen.GetProperty("mainDownload").GetString()!;
			log($"Everest {Branch}: {url}");

			string zipPath = Path.Combine(context.CacheDir!.AbsolutePath, "everest.zip");
			using (HttpResponseMessage response = http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult())
			{
				response.EnsureSuccessStatusCode();
				long total = response.Content.Headers.ContentLength ?? -1;
				using Stream input = response.Content.ReadAsStream();
				using FileStream output = File.Create(zipPath);
				byte[] buffer = new byte[1 << 16];
				long done = 0;
				int read;
				while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
				{
					output.Write(buffer, 0, read);
					done += read;
					progress(L.EverestDownloading, total > 0 ? done / (float)total : -1);
				}
			}

			string target = EverestDir(context);
			string staging = target + ".new";
			if (Directory.Exists(staging))
				Directory.Delete(staging, recursive: true);
			Directory.CreateDirectory(staging);
			string stagingFull = Path.GetFullPath(staging) + Path.DirectorySeparatorChar;

			using (ZipArchive zip = ZipFile.OpenRead(zipPath))
			{
				var files = zip.Entries.Where(e => e.Name.Length > 0).ToList();
				string prefix = "";
				string first = files[0].FullName;
				int slash = first.IndexOf('/');
				if (slash > 0 && files.All(e => e.FullName.StartsWith(first[..(slash + 1)], StringComparison.Ordinal)))
					prefix = first[..(slash + 1)];

				foreach (ZipArchiveEntry entry in files)
				{
					string dest = Path.GetFullPath(Path.Combine(staging, entry.FullName[prefix.Length..]));
					if (!dest.StartsWith(stagingFull, StringComparison.Ordinal))
						continue;
					Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
					entry.ExtractToFile(dest, overwrite: true);
				}
			}
			File.Delete(zipPath);

			if (!File.Exists(Path.Combine(staging, "Celeste.Mod.mm.dll")))
				throw new InstallException("O zip do Everest não contém Celeste.Mod.mm.dll.");

			if (Directory.Exists(target))
				Directory.Delete(target, recursive: true);
			Directory.Move(staging, target);
			Directory.CreateDirectory(ModsDir(context));
			log("Everest extraído em " + target);
			return target;
		}
	}
}
