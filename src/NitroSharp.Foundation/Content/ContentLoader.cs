﻿using System.IO;

namespace NitroSharp.Foundation.Content
{
    public abstract class ContentLoader
    {
        public abstract object Load(Stream stream);
    }
}