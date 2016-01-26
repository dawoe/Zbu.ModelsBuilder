﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Web.Compilation;
using System.Web.Hosting;
using Umbraco.Core;
using Umbraco.Core.Configuration;
using Umbraco.Core.Logging;
using Umbraco.Core.Models;
using Umbraco.Core.Models.PublishedContent;
using Umbraco.Web.Cache;
using Umbraco.ModelsBuilder.AspNet;
using Umbraco.ModelsBuilder.Building;
using Umbraco.ModelsBuilder.Configuration;
using File = System.IO.File;

namespace Umbraco.ModelsBuilder.Umbraco
{
    class PureLiveModelFactory : IPublishedContentModelFactory
    {
        private Dictionary<string, Func<IPublishedContent, IPublishedContent>> _constructors;
        private readonly ReaderWriterLockSlim _locker = new ReaderWriterLockSlim();
        private readonly IPureLiveModelsEngine[] _engines;
        private bool _hasModels;
        private bool _pendingRebuild;
        private readonly ProfilingLogger _logger;

        public PureLiveModelFactory(ProfilingLogger logger, params IPureLiveModelsEngine[] engines)
        {
            _logger = logger;
            _engines = engines;
            ContentTypeCacheRefresher.CacheUpdated += (sender, args) => ResetModels();
            DataTypeCacheRefresher.CacheUpdated += (sender, args) => ResetModels();
        }

        #region IPublishedContentModelFactory

        public IPublishedContent CreateModel(IPublishedContent content)
        {
            // get models, rebuilding them if needed
            var constructors = EnsureModels();
            if (constructors == null)
                return null;

            // be case-insensitive
            var contentTypeAlias = content.DocumentTypeAlias;

            // lookup model constructor (else null)
            Func<IPublishedContent, IPublishedContent> constructor;
            constructors.TryGetValue(contentTypeAlias, out constructor);

            // create model
            return constructor == null ? content : constructor(content);
        }

        #endregion

        #region Compilation

        // tells the factory that it should build a new generation of models
        private void ResetModels()
        {
            _logger.Logger.Debug<PureLiveModelFactory>("Resetting models.");
            _locker.EnterWriteLock();
            try
            {
                _hasModels = false;
                _pendingRebuild = true;
            }
            finally
            {
                _locker.ExitWriteLock();
            }
        }

        // ensure that the factory is running with the lastest generation of models
        private Dictionary<string, Func<IPublishedContent, IPublishedContent>> EnsureModels()
        {
            _logger.Logger.Debug<PureLiveModelFactory>("Ensuring models.");
            _locker.EnterReadLock();
            try
            {
                if (_hasModels) return _constructors;
            }
            finally
            {
                _locker.ExitReadLock();
            }

            _locker.EnterWriteLock();
            try
            {
                if (_hasModels) return _constructors;

                // we don't have models,
                // either they haven't been loaded from the cache yet
                // or they have been reseted and are pending a rebuild

                // this will lock the engines - must take care that whatever happens,
                // we unlock them - even if generation failed for some reason
                foreach (var engine in _engines)
                    engine.NotifyRebuilding();

                try
                {
                    using (_logger.DebugDuration<PureLiveModelFactory>("Get models.", "Got models."))
                    {
                        var assembly = GetModelsAssembly(_pendingRebuild);
                        var types = assembly.ExportedTypes.Where(x => x.Inherits<PublishedContentModel>());
                        _constructors = RegisterModels(types);
                        _hasModels = true;
                    }
                }
                finally
                {
                    foreach (var engine in _engines)
                        engine.NotifyRebuilt();
                }

                return _constructors;
            }
            finally
            {
                _locker.ExitWriteLock();
            }
        }

        private Assembly GetModelsAssembly(bool forceRebuild)
        {
            var appData = HostingEnvironment.MapPath("~/App_Data");
            if (appData == null)
                throw new Exception("Panic: appData is null.");

            var modelsDirectory = Path.Combine(appData, "Models");
            if (!Directory.Exists(modelsDirectory))
                Directory.CreateDirectory(modelsDirectory);

            // must filter out *.generated.cs because we haven't deleted them yet!
            var ourFiles = Directory.Exists(modelsDirectory)
                ? Directory.GetFiles(modelsDirectory, "*.cs")
                    .Where(x => !x.EndsWith(".generated.cs"))
                    .ToDictionary(x => x, File.ReadAllText)
                : new Dictionary<string, string>();

            var umbraco = Application.GetApplication();
            var typeModels = umbraco.GetAllTypes();
            var currentHash = Hash(ourFiles, typeModels);
            var modelsHashFile = Path.Combine(modelsDirectory, "models.hash");
            var modelsDllFile = Path.Combine(modelsDirectory, "models.dll");
            Assembly assembly;

            if (forceRebuild == false)
            {
                assembly = TryToLoadCachedModelsAssembly(modelsHashFile, modelsDllFile, currentHash);
                if (assembly != null)
                    return assembly;
            }

            // need to rebuild
            _logger.Logger.Debug<PureLiveModelFactory>("Rebuilding models.");

            // generate code
            var code = GenerateModelsCode(ourFiles, typeModels);
            code = RoslynRazorViewCompiler.PrepareCodeForCompilation(code);

            // save code for debug purposes
            var modelsCodeFile = Path.Combine(modelsDirectory, "models.generated.cs");
            File.WriteAllText(modelsCodeFile, code);

            // compile and register
            byte[] bytes;
            assembly = RoslynRazorViewCompiler.CompileAndRegisterModels(code, out bytes);

            // assuming we can write and it's not going to cause exceptions...
            File.WriteAllBytes(modelsDllFile, bytes);
            File.WriteAllText(modelsHashFile, currentHash);

            _logger.Logger.Debug<PureLiveModelFactory>("Done rebuilding.");
            return assembly;
        }

        private Assembly TryToLoadCachedModelsAssembly(string modelsHashFile, string modelsDllFile, string currentHash)
        {
            _logger.Logger.Debug<PureLiveModelFactory>("Looking for cached models.");

            if (!File.Exists(modelsHashFile) || !File.Exists(modelsDllFile))
            {
                _logger.Logger.Debug<PureLiveModelFactory>("Could not find cached models.");
                return null;
            }

            var cachedHash = File.ReadAllText(modelsHashFile);
            if (currentHash != cachedHash)
            {
                _logger.Logger.Debug<PureLiveModelFactory>("Found obsolete cached models.");
                return null;
            }

            _logger.Logger.Debug<PureLiveModelFactory>("Found cached models, loading.");
            try
            {
                var rawAssembly = File.ReadAllBytes(modelsDllFile);
                var assembly = RoslynRazorViewCompiler.RegisterModels(rawAssembly);
                return assembly;
            }
            catch (Exception e)
            {
                _logger.Logger.Error<PureLiveModelFactory>("Failed to load cached models.", e);
                return null;
            }
        }

        private static Dictionary<string, Func<IPublishedContent, IPublishedContent>> RegisterModels(IEnumerable<Type> types)
        {
            var ctorArgTypes = new[] { typeof(IPublishedContent) };
            var constructors = new Dictionary<string, Func<IPublishedContent, IPublishedContent>>(StringComparer.InvariantCultureIgnoreCase);

            foreach (var type in types)
            {
                var constructor = type.GetConstructor(ctorArgTypes);
                if (constructor == null)
                    throw new InvalidOperationException($"Type {type.FullName} is missing a public constructor with one argument of type IPublishedContent.");
                var attribute = type.GetCustomAttribute<PublishedContentModelAttribute>(false);
                var typeName = attribute == null ? type.Name : attribute.ContentTypeAlias;

                if (constructors.ContainsKey(typeName))
                    throw new InvalidOperationException($"More that one type want to be a model for content type {typeName}.");

                var exprArg = Expression.Parameter(typeof(IPublishedContent), "content");
                var exprNew = Expression.New(constructor, exprArg);
                var expr = Expression.Lambda<Func<IPublishedContent, IPublishedContent>>(exprNew, exprArg);
                var func = expr.Compile();
                constructors[typeName] = func;
            }

            return constructors.Count > 0 ? constructors : null;
        }

        private static string GenerateModelsCode(IDictionary<string, string> ourFiles, IList<TypeModel> typeModels)
        {
            var appData = HostingEnvironment.MapPath("~/App_Data");
            if (appData == null)
                throw new Exception("Panic: appData is null.");

            var modelsDirectory = Path.Combine(appData, "Models");
            if (!Directory.Exists(modelsDirectory))
                Directory.CreateDirectory(modelsDirectory);

            foreach (var file in Directory.GetFiles(modelsDirectory, "*.generated.cs"))
                File.Delete(file);

            // using BuildManager references
            var referencedAssemblies = BuildManager.GetReferencedAssemblies().Cast<Assembly>().ToArray();

            var parseResult = new CodeParser().Parse(ourFiles, referencedAssemblies);
            var builder = new TextBuilder(typeModels, parseResult, UmbracoConfig.For.ModelsBuilder().ModelsNamespace);

            var codeBuilder = new StringBuilder();
            builder.Generate(codeBuilder, builder.GetModelsToGenerate());
            var code = codeBuilder.ToString();

            return code;
        }

        #endregion

        #region Hashing

        private static string Hash(IDictionary<string, string> ourFiles, IEnumerable<TypeModel> typeModels)
        {
            var hash = new HashCodeCombiner();

            foreach (var kvp in ourFiles)
                hash.Add(kvp.Key + "::" + kvp.Value);

            // see Umbraco.ModelsBuilder.Umbraco.Application for what's important to hash
            // ie what comes from Umbraco (not computed by ModelsBuilder) and makes a difference

            foreach (var typeModel in typeModels.OrderBy(x => x.Alias))
            {
                hash.Add("--- CONTENT TYPE MODEL ---");
                hash.Add(typeModel.Id);
                hash.Add(typeModel.Alias);
                hash.Add(typeModel.ClrName);
                hash.Add(typeModel.ParentId);
                hash.Add(typeModel.Name);
                hash.Add(typeModel.Description);
                hash.Add(typeModel.ItemType.ToString());
                hash.Add("MIXINS:" + string.Join(",", typeModel.MixinTypes.OrderBy(x => x.Id).Select(x => x.Id)));

                foreach (var prop in typeModel.Properties.OrderBy(x => x.Alias))
                {
                    hash.Add("--- PROPERTY ---");
                    hash.Add(prop.Alias);
                    hash.Add(prop.ClrName);
                    hash.Add(prop.Name);
                    hash.Add(prop.Description);
                    hash.Add(prop.ClrType.FullName);
                }
            }

            return hash.GetCombinedHashCode();
        }

        #endregion
    }
}
