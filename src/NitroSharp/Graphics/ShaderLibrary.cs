﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Veldrid;

namespace NitroSharp.Graphics
{
    internal sealed class ShaderLibrary : IDisposable
    {
        private static readonly Assembly s_assembly = typeof(ShaderLibrary).Assembly;

        private readonly Dictionary<string, (Shader vs, Shader fs)> _shaderSetCache;
        private object _shaderLoading = new object();

        public ShaderLibrary(GraphicsDevice graphicsDevice)
        {
            GraphicsDevice = graphicsDevice;
            _shaderSetCache = new Dictionary<string, (Shader vs, Shader fs)>();
        }

        public GraphicsDevice GraphicsDevice { get; }

        public (Shader vs, Shader fs) GetShaderSet(string name)
        {
            lock (_shaderLoading)
            {
                if (!_shaderSetCache.TryGetValue(name, out (Shader vs, Shader fs) shaderSet))
                {
                    Shader vs = LoadShader(name, ShaderStages.Vertex, "VS");
                    Shader fs = LoadShader(name, ShaderStages.Fragment, "FS");
                    shaderSet = (vs, fs);
                    _shaderSetCache.Add(name, shaderSet);
                }

                return shaderSet;
            }
        }

        private Shader LoadShader(string set, ShaderStages stage, string entryPoint)
        {
            //if (set == "Cube")
            //{
            //    set = "Generated." + set.ToLower();
            //}

            ResourceFactory factory = GraphicsDevice.ResourceFactory;
            string name = "NitroSharp.Graphics.Shaders." + set +
                $"-{stage.ToString().ToLower()}{GetExtension(factory.BackendType)}";

            using (var stream = s_assembly.GetManifestResourceStream(name))
            using (var reader = new BinaryReader(stream))
            {
                byte[] bytes = reader.ReadBytes((int)stream.Length);
                return factory.CreateShader(new ShaderDescription(stage, bytes, entryPoint));
            }
        }

        private static string GetExtension(GraphicsBackend backend)
        {
            switch (backend)
            {
                case GraphicsBackend.Direct3D11: return ".hlsl.bytes";
                case GraphicsBackend.Vulkan: return ".450.glsl.spv";
                case GraphicsBackend.OpenGL: return ".330.glsl";
                case GraphicsBackend.OpenGLES: return ".300.glsles";
                case GraphicsBackend.Metal: return ".metallib";
                default: return ThrowIllegalValue(nameof(backend));
            }
        }

        public void Dispose()
        {
            foreach ((Shader vs, Shader fs) shaderSet in _shaderSetCache.Values)
            {
                shaderSet.vs.Dispose();
                shaderSet.fs.Dispose();
            }
            _shaderSetCache.Clear();
        }

        private static string ThrowIllegalValue(string paramName)
            => throw new ArgumentException("Illegal value.", paramName);

        private static void ThrowShaderSetNotFound(string name)
            => throw new InvalidOperationException($"Couldn't find shader set '{name}'");
    }
}
