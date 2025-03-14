// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace System.IO
{
    /* This class is used by for reading from the stdin when it is a terminal.
     * It is designed to read stdin in raw mode for interpreting
     * key press events and maintain its own buffer for the same.
     * which is then used for all the Read operations
     */
    internal sealed class StdInReader : TextReader
    {
        private static string? s_moveLeftString; // string written to move the cursor to the left
        private static string? s_clearToEol;     // string written to clear from cursor to end of line

        private readonly StringBuilder _readLineSB; // SB that holds readLine output.  This is a field simply to enable reuse; it's only used in ReadLine.
        private readonly Stack<ConsoleKeyInfo> _tmpKeys = new Stack<ConsoleKeyInfo>(); // temporary working stack; should be empty outside of ReadLine
        private readonly Stack<ConsoleKeyInfo> _availableKeys = new Stack<ConsoleKeyInfo>(); // a queue of already processed key infos available for reading
        private readonly Decoder _decoder;
        private readonly Encoding _encoding;
        private readonly Encoder _echoEncoder;
        private Encoder? _bufferReadEncoder;

        private char[] _unprocessedBufferToBeRead; // Buffer that might have already been read from stdin but not yet processed.
        private const int BytesToBeRead = 1024; // No. of bytes to be read from the stream at a time.
        private int _startIndex; // First unprocessed index in the buffer;
        private int _endIndex; // Index after last unprocessed index in the buffer;

        internal StdInReader(Encoding encoding)
        {
            Debug.Assert(!Console.IsInputRedirected); // stdin is a terminal.

            _encoding = encoding;
            _unprocessedBufferToBeRead = new char[encoding.GetMaxCharCount(BytesToBeRead)];
            _startIndex = 0;
            _endIndex = 0;
            _readLineSB = new StringBuilder();
            _echoEncoder = _encoding.GetEncoder();
            _decoder = _encoding.GetDecoder();
        }

        /// <summary> Checks whether the unprocessed buffer is empty. </summary>
        internal bool IsUnprocessedBufferEmpty()
        {
            return _startIndex >= _endIndex; // Everything has been processed;
        }

        internal void AppendExtraBuffer(ReadOnlySpan<byte> buffer)
        {
            // Most inputs to this will have a buffer length of one.
            // The cases where it is larger than one only occur in ReadKey
            // when the input is not redirected, so those cases should be
            // rare, so just allocate.
            const int MaxStackAllocation = 256;
            int maxCharsCount = _encoding.GetMaxCharCount(buffer.Length);
            Span<char> chars = (uint)maxCharsCount <= MaxStackAllocation ?
                stackalloc char[MaxStackAllocation] :
                new char[maxCharsCount];
            int charLen = _decoder.GetChars(buffer, chars, flush: false);
            chars = chars.Slice(0, charLen);

            // Ensure our buffer is large enough to hold all of the data
            if (IsUnprocessedBufferEmpty())
            {
                _startIndex = _endIndex = 0;
            }
            else
            {
                Debug.Assert(_endIndex > 0);
                int spaceRemaining = _unprocessedBufferToBeRead.Length - _endIndex;
                if (spaceRemaining < chars.Length)
                {
                    Array.Resize(ref _unprocessedBufferToBeRead, _unprocessedBufferToBeRead.Length * 2);
                }
            }

            // Copy the data into our buffer
            chars.CopyTo(_unprocessedBufferToBeRead.AsSpan(_endIndex));
            _endIndex += charLen;
        }

        internal static unsafe int ReadStdin(byte* buffer, int bufferSize)
        {
            int result = Interop.CheckIo(Interop.Sys.ReadStdin(buffer, bufferSize));
            Debug.Assert(result >= 0 && result <= bufferSize); // may be 0 if hits EOL
            return result;
        }

        public override string? ReadLine()
        {
            bool isEnter = ReadLineCore(consumeKeys: true);
            string? line = null;
            if (isEnter || _readLineSB.Length > 0)
            {
                line = _readLineSB.ToString();
                _readLineSB.Clear();
            }
            return line;
        }

        public int ReadLine(Span<byte> buffer)
        {
            if (buffer.IsEmpty)
            {
                return 0;
            }

            // Don't read a new line if there are remaining characters in the StringBuilder.
            if (_readLineSB.Length == 0)
            {
                bool isEnter = ReadLineCore(consumeKeys: true);
                if (isEnter)
                {
                    _readLineSB.Append('\n');
                }
            }

            // Encode line into buffer.
            Encoder encoder = _bufferReadEncoder ??= _encoding.GetEncoder();
            int bytesUsedTotal = 0;
            int charsUsedTotal = 0;
            foreach (ReadOnlyMemory<char> chunk in _readLineSB.GetChunks())
            {
                Debug.Assert(!buffer.IsEmpty);

                encoder.Convert(chunk.Span, buffer, flush: false, out int charsUsed, out int bytesUsed, out bool completed);
                buffer = buffer.Slice(bytesUsed);
                bytesUsedTotal += bytesUsed;
                charsUsedTotal += charsUsed;

                if (!completed || buffer.IsEmpty)
                {
                    break;
                }
            }
            _readLineSB.Remove(0, charsUsedTotal);
            return bytesUsedTotal;
        }

        // Reads a line in _readLineSB when consumeKeys is true,
        //              or _availableKeys when consumeKeys is false.
        // Returns whether the line was terminated using the Enter key.
        private bool ReadLineCore(bool consumeKeys)
        {
            Debug.Assert(_tmpKeys.Count == 0);
            Debug.Assert(consumeKeys || _availableKeys.Count == 0);

            // _availableKeys either contains a line that was already read,
            // or we need to read a new line from stdin.
            bool freshKeys = _availableKeys.Count == 0;

           // Don't carry over chars from previous ReadLine call.
            _readLineSB.Clear();

            Interop.Sys.InitializeConsoleBeforeRead();
            try
            {
                // Read key-by-key until we've read a line.
                while (true)
                {
                    ConsoleKeyInfo keyInfo = freshKeys ? ReadKey() : _availableKeys.Pop();
                    if (!consumeKeys && keyInfo.Key != ConsoleKey.Backspace) // backspace is the only character not written out in the below if/elses.
                    {
                        _tmpKeys.Push(keyInfo);
                    }

                    // Handle the next key.  Since for other functions we may have ended up reading some of the user's
                    // input, we need to be able to handle manually processing that input, and so we do that processing
                    // for all input.  As such, we need to special-case a few characters, e.g. recognizing when Enter is
                    // pressed to end a line.  We also need to handle Backspace specially, to fix up both our buffer of
                    // characters and the position of the cursor.  More advanced processing would be possible, but we
                    // try to keep this very simple, at least for now.
                    if (keyInfo.Key == ConsoleKey.Enter)
                    {
                        if (freshKeys)
                        {
                            EchoToTerminal('\n');
                        }
                        return true;
                    }
                    else if (IsEol(keyInfo.KeyChar))
                    {
                        return false;
                    }
                    else if (keyInfo.Key == ConsoleKey.Backspace)
                    {
                        bool removed = false;
                        if (consumeKeys)
                        {
                            int len = _readLineSB.Length;
                            if (len > 0)
                            {
                                _readLineSB.Length = len - 1;
                                removed = true;
                            }
                        }
                        else
                        {
                            removed = _tmpKeys.TryPop(out _);
                        }

                        if (removed && freshKeys)
                        {
                            // The ReadLine input may wrap across terminal rows and we need to handle that.
                            // note: ConsolePal will cache the cursor position to avoid making many slow cursor position fetch operations.
                            if (ConsolePal.TryGetCursorPosition(out int left, out int top, reinitializeForRead: true) &&
                                left == 0 && top > 0)
                            {
                                s_clearToEol ??= ConsolePal.TerminalFormatStringsInstance.ClrEol ?? string.Empty;

                                // Move to end of previous line
                                ConsolePal.SetTerminalCursorPosition(ConsolePal.WindowWidth - 1, top - 1);
                                // Clear from cursor to end of the line
                                ConsolePal.WriteTerminalAnsiString(s_clearToEol, mayChangeCursorPosition: false);
                            }
                            else
                            {
                                if (s_moveLeftString == null)
                                {
                                    string? moveLeft = ConsolePal.TerminalFormatStringsInstance.CursorLeft;
                                    s_moveLeftString = !string.IsNullOrEmpty(moveLeft) ? moveLeft + " " + moveLeft : string.Empty;
                                }

                                ConsolePal.WriteTerminalAnsiString(s_moveLeftString);
                            }
                        }
                    }
                    else if (keyInfo.Key == ConsoleKey.Tab)
                    {
                        if (consumeKeys)
                        {
                            _readLineSB.Append(keyInfo.KeyChar);
                        }
                        if (freshKeys)
                        {
                            EchoToTerminal(' ');
                        }
                    }
                    else if (keyInfo.Key == ConsoleKey.Clear)
                    {
                        _readLineSB.Clear();
                        if (freshKeys)
                        {
                            ConsolePal.WriteTerminalAnsiString(ConsolePal.TerminalFormatStringsInstance.Clear);
                        }
                    }
                    else if (keyInfo.KeyChar != '\0')
                    {
                        if (consumeKeys)
                        {
                            _readLineSB.Append(keyInfo.KeyChar);
                        }
                        if (freshKeys)
                        {
                            EchoToTerminal(keyInfo.KeyChar);
                        }
                    }
                }
            }
            finally
            {
                Interop.Sys.UninitializeConsoleAfterRead();

                // If we're not consuming the read input, make the keys available for a future read
                while (_tmpKeys.Count > 0)
                {
                    _availableKeys.Push(_tmpKeys.Pop());
                }
            }
        }

        public override int Read() => ReadOrPeek(peek: false);

        public override int Peek() => ReadOrPeek(peek: true);

        private int ReadOrPeek(bool peek)
        {
            // If there aren't any keys in our processed keys stack, read a line to populate it.
            if (_availableKeys.Count == 0)
            {
                ReadLineCore(consumeKeys: false);
            }

            // Now if there are keys, use the first.
            if (_availableKeys.Count > 0)
            {
                ConsoleKeyInfo keyInfo = peek ? _availableKeys.Peek() : _availableKeys.Pop();
                if (!IsEol(keyInfo.KeyChar))
                {
                    return keyInfo.KeyChar;
                }
            }

            // EOL
            return -1;
        }

        private static bool IsEol(char c)
        {
            return
                c != ConsolePal.s_posixDisableValue &&
                (c == ConsolePal.s_veolCharacter || c == ConsolePal.s_veol2Character || c == ConsolePal.s_veofCharacter);
        }

        /// <summary>
        /// Try to intercept the key pressed.
        ///
        /// Unlike Windows, Unix has no concept of virtual key codes.
        /// Hence, in case we do not recognize a key, we can't really
        /// get the ConsoleKey key code associated with it.
        /// As a result, we try to recognize the key, and if that does
        /// not work, we simply return the char associated with that
        /// key with ConsoleKey set to default value.
        /// </summary>
        public ConsoleKeyInfo ReadKey(bool intercept)
        {
            if (_availableKeys.Count > 0)
            {
                return _availableKeys.Pop();
            }

            ConsoleKeyInfo keyInfo = ReadKey();

            if (!intercept && keyInfo.KeyChar != '\0')
            {
                EchoToTerminal(keyInfo.KeyChar);
            }

            return keyInfo;
        }

        private unsafe ConsoleKeyInfo ReadKey()
        {
            Debug.Assert(_availableKeys.Count == 0);

            Interop.Sys.InitializeConsoleBeforeRead();
            try
            {
                if (IsUnprocessedBufferEmpty())
                {
                    // Read in bytes
                    byte* bufPtr = stackalloc byte[BytesToBeRead];
                    int result = ReadStdin(bufPtr, BytesToBeRead);
                    if (result > 0)
                    {
                        // Append them
                        AppendExtraBuffer(new ReadOnlySpan<byte>(bufPtr, result));
                    }
                    else
                    {
                        // Could be empty if EOL entered on its own.  Pick one of the EOL characters we have,
                        // or just use 0 if none are available.
                        return new ConsoleKeyInfo((char)
                            (ConsolePal.s_veolCharacter != ConsolePal.s_posixDisableValue ? ConsolePal.s_veolCharacter :
                             ConsolePal.s_veol2Character != ConsolePal.s_posixDisableValue ? ConsolePal.s_veol2Character :
                             ConsolePal.s_veofCharacter != ConsolePal.s_posixDisableValue ? ConsolePal.s_veofCharacter :
                             0),
                            default(ConsoleKey), false, false, false);
                    }
                }

                return KeyParser.Parse(_unprocessedBufferToBeRead, ConsolePal.TerminalFormatStringsInstance, ConsolePal.s_posixDisableValue, ConsolePal.s_veraseCharacter, ref _startIndex, _endIndex);
            }
            finally
            {
                Interop.Sys.UninitializeConsoleAfterRead();
            }
        }

        /// <summary>Gets whether there's input waiting on stdin.</summary>
        internal static bool StdinReady => Interop.Sys.StdinReady();

        private void EchoToTerminal(char c)
        {
            Span<byte> bytes = stackalloc byte[32]; // 32 bytes seems ample
            int bytesWritten = 1;
            if (Ascii.IsValid(c))
            {
                bytes[0] = (byte)c;
            }
            else
            {
                var chars = new ReadOnlySpan<char>(in c);
                bytesWritten = _echoEncoder.GetBytes(chars, bytes, flush: false);
                if (bytesWritten == 0)
                {
                    return;
                }
            }

            ConsolePal.WriteToTerminal(bytes.Slice(0, bytesWritten));
        }
    }
}
