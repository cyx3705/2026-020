using System.Text;

namespace HistoryJanus.Git;

/// <summary>
/// git 打印出来的路径的还原。
///
/// <see cref="GitRunner"/> 统一带上 <c>core.quotepath=false</c>，中文路径因此按原样输出；
/// 但那个开关只管非 ASCII 字节，**引号、反斜杠和控制字符仍然会让 git 把整条路径引起来**
/// 并做 C 风格转义。此时路径以 <c>"</c> 开头，原样显示就会多出一对引号和 <c>\"</c> 这类残留。
///
/// 只处理「整条被引起来」这一种形状；没被引起来的路径原样返回，绝不猜测。
/// </summary>
internal static class GitPath
{
    public static string Unquote(string value)
    {
        if (value.Length < 2 || value[0] != '"' || value[^1] != '"')
            return value;

        var body = value[1..^1];
        var text = new StringBuilder(body.Length);
        // 非 ASCII 走 \ooo 八进制，且一个字符可能拆成多个字节，必须先攒字节再整体按 UTF-8 解。
        var bytes = new List<byte>();

        void FlushBytes()
        {
            if (bytes.Count == 0)
                return;
            text.Append(Encoding.UTF8.GetString(bytes.ToArray()));
            bytes.Clear();
        }

        for (var i = 0; i < body.Length; i++)
        {
            if (body[i] != '\\')
            {
                FlushBytes();
                text.Append(body[i]);
                continue;
            }

            if (i + 1 >= body.Length)
            {
                FlushBytes();
                text.Append('\\');
                break;
            }

            var escape = body[++i];
            if (escape is >= '0' and <= '7')
            {
                var octal = 0;
                var digits = 0;
                while (digits < 3 && i < body.Length && body[i] is >= '0' and <= '7')
                {
                    octal = octal * 8 + (body[i] - '0');
                    digits++;
                    i++;
                }
                i--;
                bytes.Add((byte)octal);
                continue;
            }

            FlushBytes();
            text.Append(escape switch
            {
                'n' => '\n',
                't' => '\t',
                'r' => '\r',
                'a' => '\a',
                'b' => '\b',
                'f' => '\f',
                'v' => '\v',
                _ => escape,
            });
        }

        FlushBytes();
        return text.ToString();
    }
}
