using System.Collections.Generic;

namespace P5RCoop.Protocol;

/// <summary>
/// Keyboard key codes as sent in <see cref="KeyPress"/>. The values are copied verbatim from the
/// <c>Key</c> enum (<c>p5rpc.inputhook.interfaces.Inputs.Key</c>) in the p5rpc.inputhook.interfaces
/// NuGet package 1.0.0 (source: https://github.com/AnimatedSwine37/p5rpc.inputhook, file
/// p5rpc.inputhook.interfaces/Inputs.cs at commit 1e2e04ee26520b4d98040e6b48026732333d1d6e).
/// Names, including the upstream spellings ASTERIX and APOSTRAPHE, are kept identical so a value can be
/// cast straight to <c>Inputs.Key</c> on the host. The test suite checks this table against the package.
/// </summary>
public static class KeyCodes
{
    public const int A = 131076;
    public const int B = 131077;
    public const int C = 131078;
    public const int D = 131079;
    public const int E = 131080;
    public const int F = 131081;
    public const int G = 131082;
    public const int H = 131083;
    public const int I = 131084;
    public const int J = 131085;
    public const int K = 131086;
    public const int L = 131087;
    public const int M = 131088;
    public const int N = 131089;
    public const int O = 131090;
    public const int P = 131091;
    public const int Q = 131092;
    public const int R = 131093;
    public const int S = 131094;
    public const int T = 131095;
    public const int U = 131096;
    public const int V = 131097;
    public const int W = 131098;
    public const int X = 131099;
    public const int Y = 131100;
    public const int Z = 131101;
    public const int _1 = 131102;
    public const int _2 = 131103;
    public const int _3 = 131104;
    public const int _4 = 131105;
    public const int _5 = 131106;
    public const int _6 = 131107;
    public const int _7 = 131108;
    public const int _8 = 131109;
    public const int _9 = 131110;
    public const int _0 = 131111;
    public const int ENTER = 131112;
    public const int ESCAPE = 131113;
    public const int BACKSPACE = 131114;
    public const int TAB = 131115;
    public const int SPACE = 131116;
    public const int MINUS = 131117;
    public const int COMMA = 131126;
    public const int L_BRACKET = 131120;
    public const int BACKSLASH = 131121;
    public const int R_BRACKET = 131122;
    public const int SEMICOLON = 131123;
    public const int DOT = 131127;
    public const int FORWARD_SLASH = 131128;
    public const int CAPS_LOCK = 131129;
    public const int F1 = 131130;
    public const int F2 = 131131;
    public const int F3 = 131132;
    public const int F4 = 131133;
    public const int F5 = 131134;
    public const int F6 = 131135;
    public const int F7 = 131136;
    public const int F8 = 131137;
    public const int F9 = 131138;
    public const int F10 = 131139;
    public const int F11 = 131140;
    public const int F12 = 131141;
    public const int PRINT_SCREEN = 131142;
    public const int SCROLL_LOCK = 131143;
    public const int PAUSE = 131144;
    public const int INSERT = 131145;
    public const int HOME = 131146;
    public const int PAGE_UP = 131147;
    public const int DEL = 131148;
    public const int END = 131149;
    public const int PAGE_DOWN = 131150;
    public const int RIGHT = 131151;
    public const int LEFT = 131152;
    public const int DOWN = 131153;
    public const int UP = 131154;
    public const int NUM_LOCK = 131155;
    public const int NUM_FORWARD_SLASH = 131156;
    public const int ASTERIX = 131157;
    public const int NUM_MINUS = 131158;
    public const int PLUS = 131159;
    public const int NUM_ENTER = 131160;
    public const int NUM_1 = 131161;
    public const int NUM_2 = 131162;
    public const int NUM_3 = 131163;
    public const int NUM_4 = 131164;
    public const int NUM_5 = 131165;
    public const int NUM_6 = 131166;
    public const int NUM_7 = 131167;
    public const int NUM_8 = 131168;
    public const int NUM_9 = 131169;
    public const int NUM_0 = 131170;
    public const int NUM_DOT = 131171;
    public const int GRAVE = 131172;
    public const int EQUALS = 131173;
    public const int APOSTRAPHE = 131174;
    public const int L_CTRL = 131296;
    public const int L_SHIFT = 131297;
    public const int L_ALT = 131298;
    public const int L_WIN = 131299;
    public const int R_CTRL = 131300;
    public const int R_SHIFT = 131301;
    public const int R_ALT = 131302;
    public const int R_WIN = 131303;
    public const int APPLICATION = 131304;
    public const int CTRL = 131312;
    public const int SHIFT = 131313;
    public const int ALT = 131314;
    public const int WIN = 131315;

    private static readonly Dictionary<int, string> s_names = BuildNames();

    /// <summary>All codes with their upstream enum names.</summary>
    public static IReadOnlyDictionary<int, string> Names => s_names;

    /// <summary>True if <paramref name="code"/> is one of the known inputhook key codes.</summary>
    public static bool IsKnown(int code) => s_names.ContainsKey(code);

    /// <summary>Upstream enum name for <paramref name="code"/>, or the number if unknown.</summary>
    public static string NameOf(int code) => s_names.TryGetValue(code, out var name) ? name : code.ToString();

    private static Dictionary<int, string> BuildNames()
    {
        var names = new Dictionary<int, string>();
        foreach (var field in typeof(KeyCodes).GetFields())
        {
            if (field.IsLiteral && field.FieldType == typeof(int))
                names[(int)field.GetRawConstantValue()!] = field.Name;
        }
        return names;
    }
}
