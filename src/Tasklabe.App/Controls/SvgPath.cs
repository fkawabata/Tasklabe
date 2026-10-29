using System.Globalization;
using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;

namespace Tasklabe.App.Controls;

/// <summary>
/// SVG のパスの文字列（d 属性）を Win2D の図形にする。アイコンの形に使う M・L・H・V・A・Z（と小文字の相対の形）だけを読む。
/// </summary>
internal static class SvgPath
{
    public static CanvasGeometry Parse(ICanvasResourceCreator creator, string data)
    {
        using var builder = new CanvasPathBuilder(creator);
        builder.SetFilledRegionDetermination(CanvasFilledRegionDetermination.Winding);
        var reader = new Reader(data);
        var current = Vector2.Zero;
        var start = Vector2.Zero;
        bool open = false;
        char command = 'M';
        while (reader.SkipSeparators())
        {
            if (char.IsLetter(reader.Peek))
            {
                command = reader.Next();
            }

            bool relative = char.IsLower(command);
            var origin = relative ? current : Vector2.Zero;
            switch (char.ToUpperInvariant(command))
            {
                case 'M':
                    if (open)
                    {
                        builder.EndFigure(CanvasFigureLoop.Closed);
                    }

                    current = origin + new Vector2(reader.Number(), reader.Number());
                    start = current;
                    builder.BeginFigure(current);
                    open = true;

                    // M に続く座標は L として読む
                    command = relative ? 'l' : 'L';
                    break;
                case 'L':
                    current = origin + new Vector2(reader.Number(), reader.Number());
                    builder.AddLine(current);
                    break;
                case 'H':
                    current = new Vector2((relative ? current.X : 0) + reader.Number(), current.Y);
                    builder.AddLine(current);
                    break;
                case 'V':
                    current = new Vector2(current.X, (relative ? current.Y : 0) + reader.Number());
                    builder.AddLine(current);
                    break;
                case 'A':
                    float rx = reader.Number(), ry = reader.Number(), rotation = reader.Number();
                    bool large = reader.Flag(), sweep = reader.Flag();
                    current = origin + new Vector2(reader.Number(), reader.Number());
                    builder.AddArc(current, rx, ry, rotation * MathF.PI / 180, sweep ? CanvasSweepDirection.Clockwise : CanvasSweepDirection.CounterClockwise,
                        large ? CanvasArcSize.Large : CanvasArcSize.Small);
                    break;
                case 'Z':
                    builder.EndFigure(CanvasFigureLoop.Closed);
                    open = false;
                    current = start;
                    break;
                default:
                    throw new FormatException($"SVG のパスの命令 {command} は読めません。");
            }
        }

        if (open)
        {
            builder.EndFigure(CanvasFigureLoop.Closed);
        }

        return CanvasGeometry.CreatePath(builder);
    }

    private sealed class Reader(string text)
    {
        private int _i;

        public char Peek => text[_i];

        public char Next() => text[_i++];

        /// <summary>空白とカンマを飛ばす。読み終えていれば false。</summary>
        public bool SkipSeparators()
        {
            while (_i < text.Length && (char.IsWhiteSpace(text[_i]) || text[_i] == ','))
            {
                _i++;
            }

            return _i < text.Length;
        }

        /// <summary>弧の 0・1 の印。区切りなしで続けて書かれることがあるため 1 文字だけ読む。</summary>
        public bool Flag()
        {
            SkipSeparators();
            return Next() == '1';
        }

        /// <summary>数を 1 つ読む。「.75.75」のように、2 つ目の小数点から次の数とする。</summary>
        public float Number()
        {
            SkipSeparators();
            int begin = _i;
            if (_i < text.Length && text[_i] is '-' or '+')
            {
                _i++;
            }

            bool dot = false;
            while (_i < text.Length && (char.IsDigit(text[_i]) || (text[_i] == '.' && !dot)))
            {
                dot |= text[_i] == '.';
                _i++;
            }

            if (_i < text.Length && text[_i] is 'e' or 'E')
            {
                _i++;
                if (_i < text.Length && text[_i] is '-' or '+')
                {
                    _i++;
                }

                while (_i < text.Length && char.IsDigit(text[_i]))
                {
                    _i++;
                }
            }

            return float.Parse(text.AsSpan(begin, _i - begin), NumberStyles.Float, CultureInfo.InvariantCulture);
        }
    }
}
