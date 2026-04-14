using Daisi.Broski.Engine.Paint;

namespace Daisi.Broski.Cli;

/// <summary>
/// Temporary scratch: sample a region of an already-rendered
/// PNG and print the average color + non-background pixel
/// count, so we can tell from the CLI whether text made it
/// into the buffer at a given rect.
/// </summary>
internal static class ProbePixels
{
    public static int Run(string[] args)
    {
        if (args.Length < 5)
        {
            Console.Error.WriteLine("probe <png> <x> <y> <w> <h>");
            return 3;
        }
        string path = args[0];
        int x = int.Parse(args[1]);
        int y = int.Parse(args[2]);
        int w = int.Parse(args[3]);
        int h = int.Parse(args[4]);
        var bytes = File.ReadAllBytes(path);
        var buf = PngDecoder.TryDecode(bytes);
        if (buf is null) { Console.Error.WriteLine("not a readable PNG"); return 1; }
        long r = 0, g = 0, b = 0, count = 0, nonDark = 0;
        for (int py = y; py < y + h && py < buf.Height; py++)
        for (int px = x; px < x + w && px < buf.Width; px++)
        {
            int idx = (py * buf.Width + px) * 4;
            byte pr = buf.Pixels[idx], pg = buf.Pixels[idx + 1], pb = buf.Pixels[idx + 2];
            r += pr; g += pg; b += pb; count++;
            if (pr > 40 || pg > 40 || pb > 40) nonDark++;
        }
        if (count == 0) { Console.Out.WriteLine("empty region"); return 0; }
        Console.Out.WriteLine($"{w}x{h} at ({x},{y}): avg=({r / count},{g / count},{b / count})  non-dark={nonDark}/{count}");
        return 0;
    }
}
