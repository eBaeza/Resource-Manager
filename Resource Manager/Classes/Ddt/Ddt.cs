using BCnEncoder.Decoder;
using BCnEncoder.Encoder;
using BCnEncoder.ImageSharp;
using BCnEncoder.Shared;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Formats.Tga;
using SixLabors.ImageSharp.PixelFormats;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace Resource_Manager.Classes.Ddt
{
    public enum DdtFileTypeAlpha : byte
    {
        None = 0,
        Player = 1,
        Trans = 4,
        Blend = 8
    }

    public enum DdtFileTypeFormat : byte
    {
        Bgra = 1,
        Dxt1 = 4,
        Dxt1DE = 5,
        Grey = 7,
        Dxt3 = 8,
        Dxt5 = 9
    }

    public enum DdtFileTypeUsage : byte
    {
        AlphaTest = 1,
        LowDetail = 2,
        Bump = 4,
        Cube = 8
    }

    public static class DDTFileVersions
    {
        public const string V3 = "RTS3";
        public const string V4 = "RTS4";
    }

    public static class DDTFileHelper
    {
        private static Dictionary<DdtFileTypeFormat, CompressionFormat> FormatMapping = new()
            {
                { DdtFileTypeFormat.Dxt1, CompressionFormat.Bc1 },
                { DdtFileTypeFormat.Dxt1DE, CompressionFormat.Bc1WithAlpha },
                { DdtFileTypeFormat.Dxt3, CompressionFormat.Bc2 },
                { DdtFileTypeFormat.Dxt5, CompressionFormat.Bc3 },
                { DdtFileTypeFormat.Grey, CompressionFormat.R },
                { DdtFileTypeFormat.Bgra, CompressionFormat.Bgra }
            };

        public static CompressionFormat GetCompressionFormat(DdtFileTypeFormat format)
        {
            if (FormatMapping.ContainsKey(format))
            {
                return FormatMapping[format];
            }
            return CompressionFormat.Bgra;
        }
    }

    public class Ddt
    {
        public string Head { get; set; }
        public DdtFileTypeUsage Usage { get; set; }
        public DdtFileTypeAlpha Alpha { get; set; }
        public DdtFileTypeFormat Format { get; set; }
        public byte MipmapLevels { get; set; }
        public ushort BaseWidth { get; set; }
        public ushort BaseHeight { get; set; }
        public byte[] ColorTable { get; set; }

        public BitmapImage Bitmap { get; set; }

        public Image<Rgba32> mipmapMain { get; set; }


        public string DdtInfo
        {
            get
            {
                return BaseWidth.ToString() + "x" + BaseHeight.ToString() + ", " +
                    Format.ToString().ToUpper() + " (" + Head + " " +
                    ((byte)Usage).ToString() + " " +
                    ((byte)Alpha).ToString() + " " +
                    ((byte)Format).ToString() + " " +
                    (MipmapLevels).ToString() + ")";
            }
        }


        public async Task Create(byte[] source)
        {
            using (var stream = new MemoryStream(source))
            {
                using (var binaryReader = new BinaryReader(stream))
                {
                    ReadTextureInfo(binaryReader);
                    mipmapMain = await ReadPixels(binaryReader, false, CancellationToken.None);

                    using MemoryStream memory = new MemoryStream();
                    await mipmapMain.SaveAsBmpAsync(memory);
                    Bitmap = new BitmapImage();
                    Bitmap.BeginInit();
                    Bitmap.StreamSource = memory;
                    Bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    Bitmap.EndInit();
                }
            }
        }


        private void ReadTextureInfo(BinaryReader binaryReader)
        {
            Head = new string(binaryReader.ReadChars(4));
            Usage = (DdtFileTypeUsage)binaryReader.ReadByte();
            Alpha = (DdtFileTypeAlpha)binaryReader.ReadByte();
            Format = (DdtFileTypeFormat)binaryReader.ReadByte();
            MipmapLevels = binaryReader.ReadByte();
            BaseWidth = (ushort)binaryReader.ReadInt32();
            BaseHeight = (ushort)binaryReader.ReadInt32();

            if (Head == DDTFileVersions.V4)
            {
                // This array impacts the color, but not yet sure how.
                var colorTable_Size = binaryReader.ReadInt32();
                ColorTable = binaryReader.ReadBytes(colorTable_Size);
            }
        }

        private async Task<Image<Rgba32>> ReadPixels(BinaryReader binaryReader, bool decodeMipMaps, CancellationToken token)
        {
            // Read Mipmaps
            List<Tuple<int, int>> offsets = new();
            List<byte> mipmaps = new();
            var numImagesPerLevel = Usage.HasFlag(DdtFileTypeUsage.Cube) ? 6 : 1;
            for (var index = 0; index < MipmapLevels * numImagesPerLevel; index++)
            {
                var width = Math.Max(1, BaseWidth >> (index / numImagesPerLevel));
                var height = Math.Max(1, BaseHeight >> (index / numImagesPerLevel));
                var offset = binaryReader.ReadInt32();
                var length = binaryReader.ReadInt32();
                offsets.Add(new Tuple<int, int>(offset, length));
                if (!decodeMipMaps) break;
            }

            foreach (var offset in offsets)
            {
                binaryReader.BaseStream.Position = offset.Item1;
                mipmaps.AddRange(binaryReader.ReadBytes(offset.Item2));
            }

            // DDS decoder
            BcDecoder decoder = new BcDecoder();
            using (var dataStream = new MemoryStream(mipmaps.ToArray()))
            {
                var format = DDTFileHelper.GetCompressionFormat(Format);
                return await decoder.DecodeRawToImageRgba32Async(dataStream, BaseWidth, BaseHeight, format, token: token);
            }
        }

        public async Task Create(string source)
        {
            byte[] data = await File.ReadAllBytesAsync(source);
            await Create(data);
        }

        public async Task FromPicture(string source)
        {
            if (!await LoadFromJson(source))
            {
                string file_name = Path.GetFileName(source);
                var splitted_name = file_name.Split('.');
                if (splitted_name.Length != 3)
                {
                    throw new Exception("Missing filename.info.json");
                }
                var splitted_params = splitted_name[1].Split(new char[] { ',', '(', ')' });
                if (splitted_params.Length != 6)
                {
                    throw new Exception("Missing params in DDT details");
                }
                Head = DDTFileVersions.V3;
                Usage = (DdtFileTypeUsage)Convert.ToByte(splitted_params[1]);
                Alpha = (DdtFileTypeAlpha)Convert.ToByte(splitted_params[2]);
                Format = (DdtFileTypeFormat)Convert.ToByte(splitted_params[3]);
                MipmapLevels = Convert.ToByte(splitted_params[4]);
            }

            using Image<Rgba32> image = await Image.LoadAsync<Rgba32>(source);

            BaseWidth = (ushort)image.Width;
            BaseHeight = (ushort)image.Height;

            BcEncoder encoder = new BcEncoder();

            if (Usage.HasFlag(DdtFileTypeUsage.Bump) && Format == DdtFileTypeFormat.Dxt5)
            {
                throw new Exception("App doesn't support DXT5 bump conversion");
            }

            encoder.OutputOptions.GenerateMipMaps = true;
            encoder.OutputOptions.Quality = CompressionQuality.Balanced;
            encoder.OutputOptions.Format = DDTFileHelper.GetCompressionFormat(Format);
            encoder.OutputOptions.FileFormat = OutputFileFormat.Dds;
            encoder.OutputOptions.MaxMipMapLevel = MipmapLevels;

            var mipmaps = await encoder.EncodeToRawBytesAsync(image);

            using (var ms = new MemoryStream())
            {
                using (var bw = new BinaryWriter(ms))
                {
                    var headChar = new char[] { Head[0], Head[1], Head[2], Head[3] };
                    bw.Write(headChar);
                    bw.Write((byte)Usage);
                    bw.Write((byte)Alpha);
                    bw.Write((byte)Format);
                    bw.Write(MipmapLevels);
                    bw.Write((int)BaseWidth);
                    bw.Write((int)BaseHeight);

                    if (Head == DDTFileVersions.V4)
                    {
                        bw.Write(ColorTable.Length);
                        bw.Write(ColorTable);
                    }

                    var numImagesPerLevel = Usage.HasFlag(DdtFileTypeUsage.Cube) ? 6 : 1;

                    var fileInfoSize = (int)bw.BaseStream.Position;
                    var mipmapInfoSize = 8 * MipmapLevels * numImagesPerLevel;
                    var imageOffset = fileInfoSize + mipmapInfoSize;

                    for (var index = 0; index < MipmapLevels * numImagesPerLevel; ++index)
                    {
                        var arrayLength = mipmaps[index].GetLength(0);
                        bw.Write(imageOffset);
                        bw.Write(arrayLength);
                        imageOffset += arrayLength;

                    }
                    for (var index = 0; index < MipmapLevels * numImagesPerLevel; ++index)
                    {
                        bw.Write(mipmaps[index]);
                    }
                    await File.WriteAllBytesAsync(Path.Combine(Path.GetDirectoryName(source), Path.GetFileName(source).Split('.')[0] + ".ddt"), ms.ToArray());
                }
            }
        }

        public async Task SaveAsTGA(string dest)
        {
            TgaEncoder tga = new()
            {
                BitsPerPixel = TgaBitsPerPixel.Pixel32
            };
            var file_name = Path.GetFileNameWithoutExtension(dest);
            file_name = $"{file_name}.tga";
            var filePathFull = Path.Combine(Path.GetDirectoryName(dest), file_name);
            await mipmapMain.SaveAsTgaAsync(filePathFull, tga);
            await WriteFileNameData(filePathFull);
        }

        public async Task SaveAsPNG(string dest)
        {
            PngEncoder png = new()
            {
                ColorType = PngColorType.RgbWithAlpha
            };
            var file_name = Path.GetFileNameWithoutExtension(dest);
            file_name = $"{file_name}.png";
            var filePathFull = Path.Combine(Path.GetDirectoryName(dest), file_name);
            await mipmapMain.SaveAsPngAsync(filePathFull, png);
            await WriteFileNameData(filePathFull);
        }

        /*
         Below stores ddt header info for easy conversion
         */

        private class DDTFileInfo
        {
            public string Head { get; set; }
            public DdtFileTypeUsage Usage { get; set; }
            public DdtFileTypeAlpha Alpha { get; set; }
            public DdtFileTypeFormat Format { get; set; }
            public byte MipmapLevels { get; set; }
            public ushort BaseWidth { get; set; }
            public ushort BaseHeight { get; set; }
            public string? colorTable { get; set; }
        }

        private async Task WriteFileNameData(string file_name)
        {
            var info = new DDTFileInfo()
            {
                Head = Head,
                Usage = Usage,
                Alpha = Alpha,
                Format = Format,
                MipmapLevels = MipmapLevels,
                BaseWidth = BaseWidth,
                BaseHeight = BaseHeight,
                colorTable = ColorTable != null? string.Join(' ', ColorTable) : string.Empty
            };

            var data = System.Text.Json.JsonSerializer.Serialize(info);
            await File.WriteAllTextAsync($"{file_name}.info.json", data);
        }

        public async Task<bool> LoadFromJson(string file_name)
        {
            var jsonFilepath = $"{file_name}.info.json";
            if (!File.Exists(jsonFilepath))
                return false;

            var data = await File.ReadAllTextAsync(jsonFilepath);
            var info = System.Text.Json.JsonSerializer.Deserialize<DDTFileInfo>(data);
            Head = info.Head;
            Usage = info.Usage;
            Alpha = info.Alpha;
            Format = info.Format;
            MipmapLevels = info.MipmapLevels;
            BaseWidth = info.BaseWidth;
            BaseHeight = info.BaseHeight;

            if (Head == DDTFileVersions.V4)
            {
                var split = info.colorTable.Split(' ');
                ColorTable = new byte[split.Length];
                for (int i = 0; i < ColorTable.Length; ++i)
                {
                    ColorTable[i] = byte.Parse(split[i]);
                }
            }
            return true;
        }
    }
}
