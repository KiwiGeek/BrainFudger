using BrainFucker.Emitters;
using BrainFucker.Models;

namespace BrainFucker.Services;

internal static class BrainfuckCompiler
{
    private static readonly HashSet<char> SignificantTokens = ['>', '<', '+', '-', '.', ',', '[', ']'];

    public static byte[] Compile(string source, CompilerOptions options, IBinaryEmitter emitter)
    {
        string sanitized = new(source.Where(SignificantTokens.Contains).ToArray());
        ValidateLoops(sanitized);
        return emitter.EmitBinary(sanitized, options);
    }

    private static void ValidateLoops(string source)
    {
        Stack<int> stack = new();

        for (int i = 0; i < source.Length; i++)
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
