﻿using System;
using System.Runtime.CompilerServices;

namespace NitroSharp.Utilities
{
    internal struct ArrayBuilder<T>
    {
        private const uint DefaultCapacity = 4;

        public T[] _elements;

        public ArrayBuilder(int initialCapacity) : this((uint)initialCapacity)
        {
        }

        public ArrayBuilder(uint initialCapacity)
        {
            _elements = new T[initialCapacity];
            Count = 0;
        }

        public uint Count;

        public T[] UnderlyingArray => _elements;

        public ref T this[uint index] => ref _elements[index];
        public ref T this[int index] => ref _elements[index];

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ref T Add()
        {
            if (_elements == null)
            {
                _elements = new T[DefaultCapacity];
            }

            if (_elements.Length <= Count)
            {
                Array.Resize(ref _elements, (int)Count * 2);
            }

            return ref _elements[Count++];
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Add(T item)
        {
            if (_elements == null)
            {
                _elements = new T[DefaultCapacity];
            }

            if (_elements.Length <= Count)
            {
                Array.Resize(ref _elements, (int)Count * 2);
            }

            _elements[Count++] = item;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void AddRange(ReadOnlySpan<T> items)
        {
            if (_elements == null)
            {
                _elements = new T[DefaultCapacity];
            }

            if (_elements.Length <= Count)
            {
                Array.Resize(ref _elements, (int)Count * 2);
            }

            for (int i = 0; i < items.Length; i++)
            {
                _elements[Count++] = items[i];
            }
        }

        public void RemoveLast()
        {
            if (Count > 0)
            {
                Count--;
            }
        }

        public T[] ToArray()
        {
            var copy = new T[Count];
            Array.Copy(_elements, copy, Count);
            return copy;
        }

        public Span<T> AsSpan() => new Span<T>(_elements, 0, (int)Count);
        public ReadOnlySpan<T> AsReadonlySpan() => new ReadOnlySpan<T>(_elements, 0, (int)Count);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Reset()
        {
            Count = 0;
        }
    }
}
