// MwbLite: runtime values Mouse Without Borders persists between runs (previously stored inside the
// PowerToys settings.json). Kept separate from the user's settings.json so MWB never rewrites it.
using System;
using System.IO;
using System.Text;
using System.Text.Json;

namespace MouseWithoutBorders.Lite
{
    internal sealed class LiteState
    {
        public int MachineId { get; set; }

        /// <summary>Serialized MachinePool ("NAME:ID,..."), learned at runtime.</summary>
        public string MachinePool { get; set; } = ":,:,:,:";

        public int LastX { get; set; }

        public int LastY { get; set; }

        public int PackageId { get; set; }

        public bool FirstCtrlShiftS { get; set; }

        internal static LiteState Load(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    return JsonSerializer.Deserialize<LiteState>(File.ReadAllText(path, Encoding.UTF8), LiteConfig.JsonOptions) ?? new LiteState();
                }
            }
            catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
            {
                LiteLog.Write(nameof(LiteState), $"Ignoring unreadable {path}: {e.Message}");
            }

            return new LiteState();
        }

        internal void Save(string path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, LiteConfig.JsonOptions), new UTF8Encoding(false));
            File.Move(tmp, path, overwrite: true);
        }

        internal LiteState Clone() => (LiteState)MemberwiseClone();
    }
}
