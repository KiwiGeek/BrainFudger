namespace BrainFucker;

internal static class BrainfuckCompiler
{
    private static readonly HashSet<char> SignificantTokens = ['>', '<', '+', '-', '.', ',', '[', ']'];

    public static byte[] Compile(string source, CompilerOptions options, IBinaryEmitter emitter)
    {
        var sanitized = new string(source.Where(SignificantTokens.Contains).ToArray());
        ValidateLoops(sanitized);
        return emitter.EmitBinary(sanitized, options);
    }

    private static void ValidateLoops(string source)
    {
        var stack = new Stack<int>();

        for (var i = 0; i < source.Length; i++)
        {
            switch (source[i])
            {
                case '[':
                    stack.Push(i);
                    break;

                case ']':
                    if (stack.Count == 0)
                    {
                        throw new InvalidOperationException($"Unmatched closing bracket at Brainfuck instruction {i}.");
                    }

                    stack.Pop();
                    break;
            }
        }

        if (stack.Count > 0)
        {
            throw new InvalidOperationException($"Unmatched opening bracket at Brainfuck instruction {stack.Peek()}.");
        }
    }
}
