﻿using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using NitroSharp.Utilities;
using Veldrid;

namespace NitroSharp.Graphics
{
    [StructLayout(LayoutKind.Auto)]
    internal ref struct RenderBucketSubmission<TVertex>
        where TVertex : unmanaged
    {
        public VertexBuffer VertexBuffer;
        public DeviceBuffer IndexBuffer;
        public Pipeline Pipeline;
        public ResourceSet SharedResourceSet;
        public ResourceSet ObjectResourceSet;
        public ushort VertexBase;
        public ushort IndexBase;
        public ushort IndexCount;
    }

    [StructLayout(LayoutKind.Auto)]
    internal ref struct RenderBucketSubmission<TVertex, TInstanceData>
        where TVertex : unmanaged
        where TInstanceData : unmanaged
    {
        public VertexBuffer VertexBuffer;
        public DeviceBuffer IndexBuffer;
        public VertexBuffer InstanceDataBuffer;
        public Pipeline Pipeline;
        public ResourceSet SharedResourceSet;
        public ResourceSet ObjectResourceSet;
        public ushort VertexBase;
        public ushort IndexBase;
        public ushort IndexCount;
        public ushort InstanceBase;
        public ushort InstanceCount;
    }

    internal sealed class RenderBucket<TKey> where TKey : IComparable<TKey>
    {
        private struct RenderItem
        {
            public ResourceSet ObjectResourceSet;
            public ushort VertexBase;
            public ushort IndexBase;
            public ushort IndexCount;
            public ushort InstanceBase;
            public ushort InstanceCount;
            public byte SharedResourceSetId;
            public byte PipelineId;
            public byte VertexBuffer0;
            public byte VertexBuffer1;
            public byte IndexBuffer;
        }

        private ArrayBuilder<RenderItem> _renderItems;
        private ArrayBuilder<TKey> _keys;

        private readonly List<VertexBuffer> _vertexBuffers;
        private (byte index, VertexBuffer buffer) _lastVertexBuffer0;
        private (byte index, VertexBuffer buffer) _lastInstanceDataBuffer;

        private readonly List<DeviceBuffer> _indexBuffers;
        private (byte index, DeviceBuffer buffer) _lastIndexBuffer;

        private readonly List<Pipeline> _pipelines;
        private (byte index, Pipeline pipeline) _lastPipeline;

        private readonly List<ResourceSet> _sharedResourceSets;
        private (byte index, ResourceSet pipeline) _lastSharedResourceSet;

        public RenderBucket(uint initialCapacity)
        {
            _renderItems = new ArrayBuilder<RenderItem>(initialCapacity);
            _keys = new ArrayBuilder<TKey>(initialCapacity);
            _vertexBuffers = new List<VertexBuffer>();
            _indexBuffers = new List<DeviceBuffer>();
            _pipelines = new List<Pipeline>();
            _sharedResourceSets = new List<ResourceSet>();
        }

        public void Begin()
        {
            _renderItems.Reset();
            _keys.Reset();
            _vertexBuffers.Clear();
            _lastVertexBuffer0 = default;
            _lastInstanceDataBuffer = default;
            _indexBuffers.Clear();
            _lastIndexBuffer = default;
            _pipelines.Clear();
            _sharedResourceSets.Clear();
            _lastSharedResourceSet = default;
            _lastPipeline = default;
        }

        public void Submit<TVertex>(ref RenderBucketSubmission<TVertex> submission, TKey key)
            where TVertex : unmanaged
        {
            ref RenderItem renderItem = ref _renderItems.Add();
            renderItem.VertexBuffer0 = GetResourceId(submission.VertexBuffer, _vertexBuffers, ref _lastVertexBuffer0);
            renderItem.IndexBuffer = GetResourceId(submission.IndexBuffer, _indexBuffers, ref _lastIndexBuffer);
            renderItem.VertexBase = submission.VertexBase;
            renderItem.IndexBase = submission.IndexBase;
            renderItem.IndexCount = submission.IndexCount;
            renderItem.PipelineId = GetPipelineId(submission.Pipeline);
            renderItem.SharedResourceSetId = GetResourceId(submission.SharedResourceSet, _sharedResourceSets, ref _lastSharedResourceSet);
            renderItem.ObjectResourceSet = submission.ObjectResourceSet;

            _keys.Add(key);
        }

        public void Submit<TVertex, TInstanceData>(ref RenderBucketSubmission<TVertex, TInstanceData> submission, TKey key)
            where TVertex : unmanaged
            where TInstanceData : unmanaged
        {
            ref RenderItem renderItem = ref _renderItems.Add();
            renderItem.VertexBuffer0 = GetResourceId(submission.VertexBuffer, _vertexBuffers, ref _lastVertexBuffer0);
            renderItem.IndexBuffer = GetResourceId(submission.IndexBuffer, _indexBuffers, ref _lastIndexBuffer);
            renderItem.VertexBase = submission.VertexBase;
            renderItem.IndexBase = submission.IndexBase;
            renderItem.IndexCount = submission.IndexCount;
            renderItem.PipelineId = GetPipelineId(submission.Pipeline);
            renderItem.SharedResourceSetId = GetResourceId(submission.SharedResourceSet, _sharedResourceSets, ref _lastSharedResourceSet);
            renderItem.ObjectResourceSet = submission.ObjectResourceSet;
            renderItem.VertexBuffer1 = GetResourceId(submission.InstanceDataBuffer, _vertexBuffers, ref _lastInstanceDataBuffer);
            renderItem.InstanceBase = submission.InstanceBase;
            renderItem.InstanceCount = submission.InstanceCount;

            _keys.Add(key);
        }

        public void End(CommandList commandList)
        {
            Array.Sort(_keys.UnderlyingArray, _renderItems.UnderlyingArray, 0, (int)_renderItems.Count);

            byte lastVertexBuffer0 = byte.MaxValue;
            byte lastVertexBuffer1 = byte.MaxValue;
            byte lastIndexBuffer = byte.MaxValue;
            byte lastPipelineId = byte.MaxValue;
            byte lastSharedResourceSetId = byte.MaxValue;
            ResourceSet lastObjectResourceSet = null;
            for (uint i = 0; i < _renderItems.Count; i++)
            {
                ref RenderItem item = ref _renderItems[i];
                if (item.PipelineId != lastPipelineId)
                {
                    Pipeline pipeline = _pipelines[item.PipelineId];
                    commandList.SetPipeline(pipeline);
                    lastPipelineId = item.PipelineId;
                }

                if (item.SharedResourceSetId != lastSharedResourceSetId)
                {
                    ResourceSet set = _sharedResourceSets[item.SharedResourceSetId];
                    commandList.SetGraphicsResourceSet(0, set);
                    lastSharedResourceSetId = item.SharedResourceSetId;
                }

                ResourceSet objectResourceSet = item.ObjectResourceSet;
                if (objectResourceSet != lastObjectResourceSet)
                {
                    commandList.SetGraphicsResourceSet(1, objectResourceSet);
                    lastObjectResourceSet = objectResourceSet;
                }

                if (item.VertexBuffer0 != lastVertexBuffer0)
                {
                    DeviceBuffer buffer = _vertexBuffers[item.VertexBuffer0].DeviceBuffer;
                    commandList.SetVertexBuffer(0, buffer);
                    lastVertexBuffer0 = item.VertexBuffer0;
                }
                if (item.VertexBuffer1 != lastVertexBuffer1)
                {
                    DeviceBuffer buffer = _vertexBuffers[item.VertexBuffer1].DeviceBuffer;
                    commandList.SetVertexBuffer(1, buffer);
                    lastVertexBuffer1 = item.VertexBuffer1;
                }
                if (item.IndexBuffer != lastIndexBuffer)
                {
                    DeviceBuffer buffer = _indexBuffers[item.IndexBuffer];
                    commandList.SetIndexBuffer(buffer, IndexFormat.UInt16);
                    lastIndexBuffer = item.IndexBuffer;
                }

                uint instanceStart = i;
                commandList.DrawIndexed(
                    item.IndexCount,
                    item.InstanceCount,
                    item.IndexBase,
                    item.VertexBase,
                    item.InstanceBase);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private byte GetPipelineId(Pipeline pipeline)
            => GetResourceId(pipeline, _pipelines, ref _lastPipeline);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private byte GetResourceId<T>(T resource, List<T> resourceList, ref (byte index, T resource) lastUsed)
            where T : class
        {
            if (resource == lastUsed.resource)
            {
                return lastUsed.index;
            }

            int index;
            if ((index = resourceList.IndexOf(resource)) == -1)
            {
                index = resourceList.Count;
                resourceList.Add(resource);
                lastUsed = ((byte)index, resource);
            }

            return (byte)index;
        }
    }
}
