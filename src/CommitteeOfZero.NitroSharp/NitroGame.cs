﻿using CommitteeOfZero.NitroSharp.Foundation;
using CommitteeOfZero.NitroSharp.Foundation.Audio;
using CommitteeOfZero.NitroSharp.Foundation.Content;
using CommitteeOfZero.NitroSharp.Graphics;
using CommitteeOfZero.NsScript.Execution;
using System;
using CommitteeOfZero.NsScript;
using System.Collections.Generic;
using CommitteeOfZero.NitroSharp.Audio;
using System.IO;
using CommitteeOfZero.NitroSharp.Foundation.Animation;
using CommitteeOfZero.NitroSharp.Foundation.Graphics;
using Microsoft.Extensions.Logging;

namespace CommitteeOfZero.NitroSharp
{
    public class NitroGame : Game
    {
        private readonly NitroConfiguration _configuration;
        private readonly string _nssFolder;

        private NsScriptInterpreter _nssInterpreter;
        private NitroCore _nitroCore;
        private RenderSystem _renderSystem;

        private ILogger _interpreterLog;
        private ILogger _entityLog;

        public NitroGame(NitroConfiguration configuration)
        {
            _configuration = configuration;
            _nssFolder = Path.Combine(configuration.ContentRoot, "nss");
            SetupLogging();
        }

        protected override void SetParameters(GameParameters parameters)
        {
            parameters.WindowWidth = _configuration.WindowWidth;
            parameters.WindowHeight = _configuration.WindowHeight;
            parameters.WindowTitle = _configuration.WindowTitle;
            parameters.EnableVSync = _configuration.EnableVSync;
        }

        protected override void RegisterStartupTasks(IList<Action> tasks)
        {
            tasks.Add(() => RunStartupScript());
        }

        protected override ContentManager CreateContentManager()
        {
            var content = new ContentManager(_configuration.ContentRoot);

            var textureLoader = new WicTextureLoader(RenderContext);
            var audioLoader = new FFmpegAudioLoader();
            content.RegisterContentLoader(typeof(Texture2D), textureLoader);
            content.RegisterContentLoader(typeof(AudioStream), audioLoader);

            _nitroCore.SetContent(content);
            return content;
        }

        protected override void RegisterSystems(IList<GameSystem> systems)
        {
            var inputHandler = new InputHandler(_nitroCore);
            systems.Add(inputHandler);

            var animationSystem = new AnimationSystem();
            systems.Add(animationSystem);

            var audioSystem = new AudioSystem(AudioEngine);
            systems.Add(audioSystem);

            _renderSystem = new RenderSystem(RenderContext);
            systems.Add(_renderSystem);
        }

        private void RunStartupScript()
        {
            _nitroCore = new NitroCore(this, _configuration, Entities);
            _nssInterpreter = new NsScriptInterpreter(_nitroCore, LocateScript);
            _nssInterpreter.BuiltInCallScheduled += OnBuiltInCallDispatched;
            _nssInterpreter.EnteredFunction += OnEnteredFunction;

            _nssInterpreter.CreateThread(_configuration.StartupScript);
        }

        private Stream LocateScript(string path)
        {
            return File.OpenRead(Path.Combine(_nssFolder, path.Replace("nss/", string.Empty)));
        }

        private void OnEnteredFunction(object sender, Function function)
        {
            _interpreterLog.LogCritical($"Entered function {function.Name.SimplifiedName}");
        }

        private void OnBuiltInCallDispatched(object sender, BuiltInFunctionCall call)
        {
            if (call.CallingThreadId == _nitroCore.MainThread.Id)
            {
                _interpreterLog.LogInformation($"Built-in call: {call.ToString()}");
            }
        }

        private void SetupLogging()
        {
            var loggerFactory = new LoggerFactory().AddConsole();
            _interpreterLog = loggerFactory.CreateLogger("Interpreter");
            //_entityLog = loggerFactory.CreateLogger("Entity System");

            //Entities.EntityRemoved += (o, e) => _entityLog.LogInformation($"Removed entity '{e.Name}'");
        }

        public override void LoadCommonResources()
        {
            _renderSystem.LoadCommonResources();
        }

        public override void Update(float deltaMilliseconds)
        {
            MainLoopTaskScheduler.FlushQueuedTasks();
            _nssInterpreter.Run(TimeSpan.MaxValue);
            Systems.Update(deltaMilliseconds);
        }
    }
}
