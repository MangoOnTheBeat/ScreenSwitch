using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;

// Build-time helper: renders the app logo from Logo.cs into a multi-size .ico file.
static class MakeIcon
{
    static void Main(string[] args)
    {
        int[] sizes = { 16, 24, 32, 48, 64, 128, 256 };
        List<byte[]> images = new List<byte[]>();
        foreach (int size in sizes)
        {
            using (Bitmap bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb))
            using (Graphics g = Graphics.FromImage(bmp))
            using (MemoryStream ms = new MemoryStream())
            {
                Logo.Draw(g, size);
                bmp.Save(ms, ImageFormat.Png);
                images.Add(ms.ToArray());
            }
        }

        using (BinaryWriter w = new BinaryWriter(File.Create(args[0])))
        {
            w.Write((short)0);
            w.Write((short)1);
            w.Write((short)sizes.Length);
            int offset = 6 + 16 * sizes.Length;
            for (int i = 0; i < sizes.Length; i++)
            {
                w.Write((byte)(sizes[i] == 256 ? 0 : sizes[i]));
                w.Write((byte)(sizes[i] == 256 ? 0 : sizes[i]));
                w.Write((byte)0);
                w.Write((byte)0);
                w.Write((short)1);
                w.Write((short)32);
                w.Write(images[i].Length);
                w.Write(offset);
                offset += images[i].Length;
            }
            foreach (byte[] image in images) w.Write(image);
        }
    }
}
