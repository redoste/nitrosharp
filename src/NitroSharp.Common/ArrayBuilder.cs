﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace NitroSharp.Utilities
{
    public struct ArrayBuilder<T>
    {
        private T[] _elements;
        private uint _count;

        public ArrayBuilder(int initialCapacity) : this((uint)initialCapacity)
        {
        }

        public ArrayBuilder(uint initialCapacity)
        {
            _elements = initialCapacity > 0
                ? new T[initialCapacity]
                : Array.Empty<T>();
            _count = 0;
        }

        public T[] UnderlyingArray => _elements;
        public uint Count => _count;

        public ref T this[uint index]
        {
            get
            {
                static void oob() => throw new IndexOutOfRangeException();
                if (index >= _count) { oob(); }
                return ref _elements[index];
            }
        }

        public ref T this[Index index]
        {
            get
            {
                static void oob() => throw new IndexOutOfRangeException();
                int actualIndex = index.IsFromEnd
                    ? (int)_count - index.Value
                    : index.Value;
                if (actualIndex >= _count) { oob(); }
                return ref _elements[actualIndex];
            }
        }

        public ref T this[int index]
        {
            get
            {
                static void oob() => throw new IndexOutOfRangeException();
                if (index >= _count) { oob(); }
                return ref _elements[index];
            }
        }

        public ref T Add()
        {
            EnsureCapacity(_count + 1);
            return ref _elements[_count++];
        }

        public void Add(T item)
        {
            EnsureCapacity(_count + 1);
            _elements[_count++] = item;
        }

        public void Insert(uint index, T item)
        {
            Debug.Assert(index < _count);
            if (_count == _elements.Length)
            {
                Array.Resize(ref _elements, _elements.Length * 2);
            }

            if (index < _count)
            {
                Array.Copy(_elements, index, _elements, index + 1, _count - index);
            }

            _elements[index] = item;
            _count++;
        }

        public void RemoveAt(uint index)
        {
            Debug.Assert(index < _count);
            _count--;
            if (index < _count)
            {
                Array.Copy(_elements, index + 1, _elements, index, _count - index);
            }
        }

        public void Remove(T item)
        {
            T[] elems = _elements;
            for (uint i = 0; i < _count; i++)
            {
                if (EqualityComparer<T>.Default.Equals(elems[i], item))
                {
                    RemoveAt(i);
                    return;
                }
            }
        }

        public T SwapRemove(uint index)
        {
            ref T ptr = ref _elements[index];
            T elem = ptr;
            ref T last = ref _elements[--_count];
            ptr = last;
            last = default!;
            return elem;
        }

        public void Truncate(uint length)
        {
            static void outOfRange() => throw new ArgumentOutOfRangeException(nameof(length));

            if (length > Count) outOfRange();
            _count = length;
        }

        public void Clear()
        {
            _count = 0;
        }

        public void AddRange(ReadOnlySpan<T> items)
        {
            EnsureCapacity((uint)(_count + items.Length));
            for (int i = 0; i < items.Length; i++)
            {
                _elements[_count++] = items[i];
            }
        }

        public Span<T> Append(uint count)
        {
            EnsureCapacity(_count + count);
            var span = new Span<T>(_elements, (int)_count, (int)count);
            _count += count;
            return span;
        }

        public void RemoveLast()
        {
            if (_count > 0)
            {
                _count--;
            }
        }

        public T[] ToArray()
        {
            var copy = new T[_count];
            Array.Copy(_elements, copy, _count);
            return copy;
        }

        public Span<T> AsSpan() => new(_elements, 0, (int)_count);
        public Span<T> AsSpan(int start, int length) => new(_elements, start, length);
        public ReadOnlySpan<T> AsReadonlySpan() => new(_elements, 0, (int)_count);
        public ReadOnlySpan<T> AsReadonlySpan(int start, int length)
            => new(_elements, start, length);

        public void Reset()
        {
            _count = 0;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void EnsureCapacity(uint requiredCapacity)
        {
            if (_elements.Length < requiredCapacity)
            {
                Grow(requiredCapacity);
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void Grow(uint requiredCapacity)
        {
            uint newCapacity = Math.Max((uint)_elements.Length * 2, requiredCapacity);
            Array.Resize(ref _elements, (int)newCapacity);
        }
    }
}
