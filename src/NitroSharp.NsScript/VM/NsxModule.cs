﻿using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using NitroSharp.NsScript.Utilities;

namespace NitroSharp.NsScript.VM
{
    public enum SubroutineKind : byte
    {
        Chapter = 0,
        Scene = 1,
        Function = 2
    }

    [DebuggerDisplay("Module '{Name}'")]
    public sealed class NsxModule
    {
        private readonly string?[] _stringHeap;

        private readonly Stream _stream;
        private readonly int[] _subroutineOffsets;

        private readonly int[] _stringOffsets;
        private readonly Subroutine[] _subroutines;
        private readonly SubroutineRuntimeInfo[] _srti;
        private readonly Dictionary<string, int> _subroutineMap;

        private NsxModule(
            Stream stream, string name, DateTimeOffset sourceModificationTime,
            int[] subroutineOffsets, byte[] rtiTable, string[] imports, int[] stringOffsets)
        {
            _stream = stream;
            Name = name;
            SourceModificationTime = sourceModificationTime;
            _subroutineOffsets = subroutineOffsets;
            Imports = imports;
            _stringOffsets = stringOffsets;
            _subroutines = new Subroutine[_subroutineOffsets.Length];
            _stringHeap = new string?[stringOffsets.Length];

            int subroutineCount = _subroutines.Length;
            var rtiReader = new BufferReader(rtiTable);
            var rtiEntryOffsets = new int[subroutineCount];
            for (int i = 0; i < subroutineCount; i++)
            {
                rtiEntryOffsets[i] = rtiReader.ReadUInt16LE();
            }

            _srti = new SubroutineRuntimeInfo[subroutineCount];
            _subroutineMap = new Dictionary<string, int>(subroutineCount);
            int rtiStart = rtiReader.Position;
            for (int i = 0; i < subroutineCount; i++)
            {
                rtiReader.Position = rtiStart + rtiEntryOffsets[i];
                var rti = new SubroutineRuntimeInfo(ref rtiReader);
                _srti[i] = rti;
                _subroutineMap[rti.SubroutineName] = i;
            }
        }

        public string Name { get; }
        public string[] Imports { get; }
        public DateTimeOffset SourceModificationTime { get; }

        internal Subroutine GetSubroutine(int index)
        {
            ref Subroutine subroutine = ref _subroutines[index];
            if (subroutine.IsEmpty)
            {
                LoadSubroutine(index);
            }

            return subroutine;
        }

        public ref readonly SubroutineRuntimeInfo GetSubroutineRuntimeInfo(
            int subroutineIndex)
        {
            return ref _srti[subroutineIndex];
        }

        public string GetSubroutineName(int subroutineIndex)
            => _srti[subroutineIndex].SubroutineName;

        public string GetString(ushort token)
        {
            ref string? s = ref _stringHeap[token];
            if (s == null)
            {
                _stream.Position = _stringOffsets[token];
                int length = ReadUInt16();
                Span<byte> bytes = length <= 1024
                    ? stackalloc byte[length]
                    : new byte[length];
                _stream.Read(bytes);
                s = Encoding.UTF8.GetString(bytes);
            }

            return s;
        }

        public bool TryLookupSubroutineIndex(string name, out int index)
            => _subroutineMap.TryGetValue(name, out index);

        public int LookupSubroutineIndex(string name)
            => _subroutineMap[name];

        private void LoadSubroutine(int index)
        {
            _stream.Position = _subroutineOffsets[index];
            int size = ReadUInt16();
            var bytes = new byte[size];
            _stream.Read(bytes);
            _subroutines[index] = new Subroutine(bytes);
        }

        private ushort ReadUInt16()
        {
            Span<byte> bytes = stackalloc byte[2];
            _stream.Read(bytes);
            return BinaryPrimitives.ReadUInt16LittleEndian(bytes);
        }

        private unsafe struct TableHeader
        {
            public fixed byte Marker[4];
            public int TableSize;
        }

        public static long GetSourceModificationTime(Stream stream)
        {
            Debug.Assert(stream.Position == 0);
            Span<byte> nsxHeader = stackalloc byte[NsxConstants.NsxHeaderSize];
            stream.Read(nsxHeader);
            var headerReader = new BufferReader(nsxHeader);
            headerReader.Position += 4;
            return headerReader.ReadInt64LE();
        }

        public static NsxModule LoadModule(Stream stream, string name)
        {
            static unsafe TableHeader readTableHeader(Stream stream)
            {
                Span<byte> bytes = stackalloc byte[6];
                stream.Read(bytes);

                TableHeader header;
                bytes.Slice(0, 4).CopyTo(new Span<byte>(header.Marker, 4));
                header.TableSize = BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(4));
                return header;
            }

            static unsafe void assertMarker(ref TableHeader header, ReadOnlySpan<byte> expected)
            {
                fixed (byte* pMarker = &header.Marker[0])
                {
                    var bytes = new Span<byte>(pMarker, 4);
                    Debug.Assert(bytes.SequenceEqual(expected));
                }
            }

            Span<byte> header = stackalloc byte[NsxConstants.NsxHeaderSize];
            stream.Read(header);

            var reader = new BufferReader(header);
            ReadOnlySpan<byte> magic = reader.Consume(4);
            long unixTimestamp = reader.ReadInt64LE();
            var modificationTime = DateTimeOffset.FromUnixTimeSeconds(unixTimestamp);
            _ = reader.ReadInt32LE();
            int rtiTableOffset = reader.ReadInt32LE();

            TableHeader subHeader = readTableHeader(stream);
            assertMarker(ref subHeader, NsxConstants.SubTableMarker);
            var subTableBytes = new byte[subHeader.TableSize];
            stream.Read(subTableBytes);
            reader = new BufferReader(subTableBytes);
            int subCount = reader.ReadUInt16LE();
            var subroutineOffsets = new int[subCount];
            for (int i = 0; i < subCount; i++)
            {
                subroutineOffsets[i] = reader.ReadInt32LE();
            }

            stream.Position = rtiTableOffset;
            TableHeader rtiHeader = readTableHeader(stream);
            assertMarker(ref rtiHeader, NsxConstants.RtiTableMarker);
            var rtiBytes = new byte[rtiHeader.TableSize];
            stream.Read(rtiBytes);

            TableHeader impHeader = readTableHeader(stream);
            assertMarker(ref impHeader, NsxConstants.ImportTableMarker);
            var impBytes = new byte[impHeader.TableSize];
            stream.Read(impBytes);
            reader = new BufferReader(impBytes);
            int impEntryCount = reader.ReadUInt16LE();
            var imports = new string[impEntryCount];
            for (int i = 0; i < impEntryCount; i++)
            {
                imports[i] = reader.ReadLengthPrefixedUtf8String();
            }

            TableHeader strHeader = readTableHeader(stream);
            assertMarker(ref strHeader, NsxConstants.StringTableMarker);
            var strTableBytes = new byte[strHeader.TableSize];
            stream.Read(strTableBytes);
            reader = new BufferReader(strTableBytes);
            int stringCount = reader.ReadUInt16LE();
            var stringOffsets = new int[stringCount];
            for (int i = 0; i < stringCount; i++)
            {
                stringOffsets[i] = reader.ReadInt32LE();
            }

            return new NsxModule(
                stream,
                name,
                modificationTime,
                subroutineOffsets,
                rtiBytes,
                imports,
                stringOffsets
            );
        }
    }

    internal readonly struct Subroutine
    {
        private readonly byte[] _bytes;
        private readonly int _codeStart;

        public bool IsEmpty => _bytes == null;
        public readonly int[] DialogueBlockOffsets;

        public Subroutine(byte[] bytes)
        {
            _bytes = bytes;
            var reader = new BufferReader(bytes);
            int dialogoueBlockCount = reader.ReadUInt16LE();
            DialogueBlockOffsets = Array.Empty<int>();
            if (dialogoueBlockCount > 0)
            {
                DialogueBlockOffsets = new int[dialogoueBlockCount];
                for (int i = 0; i < dialogoueBlockCount; i++)
                {
                    DialogueBlockOffsets[i] = reader.ReadUInt16LE();
                }
            }

            _codeStart = reader.Position;
        }

        public ReadOnlySpan<byte> Code
            => new(_bytes, _codeStart, _bytes.Length - _codeStart);
    }

    [DebuggerDisplay("{SubroutineKind} '{SubroutineName}'")]
    public struct SubroutineRuntimeInfo
    {
        private string[]? _parameterNames;
        private readonly Dictionary<string, int>? _dialogueBlockMap;

        public readonly SubroutineKind SubroutineKind;
        public readonly string SubroutineName;
        public readonly (string box, string name)[] DialogueBlockInfos;

        internal SubroutineRuntimeInfo(ref BufferReader reader)
        {
            SubroutineKind = (SubroutineKind)reader.ReadByte();
            SubroutineName = reader.ReadLengthPrefixedUtf8String();
            int dialogueBlockCount = reader.ReadUInt16LE();
            DialogueBlockInfos = dialogueBlockCount > 0
                ? new (string, string)[dialogueBlockCount]
                : Array.Empty<(string, string)>();

            _dialogueBlockMap = null;
            if (dialogueBlockCount > 0)
            {
                _dialogueBlockMap = new Dictionary<string, int>(dialogueBlockCount);
                for (int i = 0; i < dialogueBlockCount; i++)
                {
                    (string box, string name) info =
                        (reader.ReadLengthPrefixedUtf8String(),
                         reader.ReadLengthPrefixedUtf8String());
                    DialogueBlockInfos[i] = info;
                    _dialogueBlockMap[info.name] = i;
                }
            }

            _parameterNames = null;
        }

        internal readonly int LookupDialogueBlockIndex(string dialogueBlockName)
        {
            Debug.Assert(_dialogueBlockMap != null);
            return _dialogueBlockMap[dialogueBlockName];
        }

        internal string[] GetParameterNames(byte[] rtiTable)
        {
            if (SubroutineKind != SubroutineKind.Function)
            {
                return Array.Empty<string>();
            }

            if (_parameterNames == null)
            {
                DecodeParameterNames(rtiTable);
                Debug.Assert(_parameterNames != null);
            }

            return _parameterNames;
        }

        private void DecodeParameterNames(byte[] rtiTable)
        {
            Debug.Assert(SubroutineKind == SubroutineKind.Function);
            var reader = new BufferReader(rtiTable);
            int parameterCount = reader.ReadByte();
            _parameterNames = parameterCount > 0
                ? new string[parameterCount]
                : Array.Empty<string>();
            for (int i = 0; i < parameterCount; i++)
            {
                _parameterNames[i] = reader.ReadLengthPrefixedUtf8String();
            }
        }
    }
}
