using HarmonyLib;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Logging;
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using static StrmAssistant.Common.CommonUtility;
using static StrmAssistant.Mod.PatchManager;

namespace StrmAssistant.Mod
{
    public class EnhanceNfoMetadata : PatchBase<EnhanceNfoMetadata>
    {
        private static Assembly _nfoMetadataAssembly;
        private static ConstructorInfo _genericBaseNfoParserConstructor;
        private static MethodInfo _getPersonFromXmlNode;

        private static readonly AsyncLocal<string> PersonContent = new AsyncLocal<string>();

        private static readonly XmlReaderSettings ReaderSettings = new XmlReaderSettings
        {
            ValidationType = ValidationType.None,
            Async = true,
            CheckCharacters = false,
            IgnoreProcessingInstructions = true,
            IgnoreComments = true
        };

        private static readonly XmlWriterSettings WriterSettings = new XmlWriterSettings
        {
            OmitXmlDeclaration = true,
            CheckCharacters = false
        };

        public EnhanceNfoMetadata()
        {
            Initialize();

            if (Plugin.Instance.MetadataEnhanceStore.GetOptions().EnhanceNfoMetadata)
            {
                Patch();
            }
        }

        protected override void OnInitialize()
        {
            _nfoMetadataAssembly = AppDomain.CurrentDomain
                .GetAssemblies()
                .FirstOrDefault(a => a.GetName().Name == "NfoMetadata");

            if (_nfoMetadataAssembly != null)
            {
                var genericBaseNfoParser = _nfoMetadataAssembly.GetType("NfoMetadata.Parsers.BaseNfoParser`1");
                var genericBaseNfoParserVideo = genericBaseNfoParser.MakeGenericType(typeof(Video));
                _genericBaseNfoParserConstructor = genericBaseNfoParserVideo.GetConstructor(
                    BindingFlags.Instance | BindingFlags.Public, null,
                    new[]
                    {
                        typeof(ILogger), typeof(IConfigurationManager), typeof(IProviderManager),
                        typeof(IFileSystem)
                    }, null);
                _getPersonFromXmlNode = genericBaseNfoParserVideo.GetMethod("GetPersonFromXmlNode",
                    BindingFlags.NonPublic | BindingFlags.Instance);
            }
            else
            {
                Plugin.Instance.Logger.Warn("EnhanceNfoMetadata - NfoMetadata plugin is not installed");
                PatchTracker.FallbackPatchApproach = PatchApproach.None;
                PatchTracker.IsSupported = false;
            }
        }

        protected override void Prepare(bool apply)
        {
            PatchUnpatch(PatchTracker, apply, _genericBaseNfoParserConstructor,
                prefix: nameof(GenericBaseNfoParserConstructorPrefix));

            if (!apply)
            {
                PatchUnpatch(PatchTracker, false, _getPersonFromXmlNode, prefix: nameof(GetPersonFromXmlNodePrefix),
                    postfix: nameof(GetPersonFromXmlNodePostfix));
            }
        }

        [HarmonyPrefix]
        private static bool GenericBaseNfoParserConstructorPrefix(object __instance)
        {
            PatchUnpatch(Instance.PatchTracker, false, _getPersonFromXmlNode,
                prefix: nameof(GetPersonFromXmlNodePrefix), postfix: nameof(GetPersonFromXmlNodePostfix),
                suppress: true);
            PatchUnpatch(Instance.PatchTracker, true, _getPersonFromXmlNode, prefix: nameof(GetPersonFromXmlNodePrefix),
                postfix: nameof(GetPersonFromXmlNodePostfix), suppress: true);

            return true;
        }

        [HarmonyPrefix]
        private static bool GetPersonFromXmlNodePrefix(ref XmlReader reader)
        {
            try
            {
                var sb = new StringBuilder();

                using (var writer = new StringWriter(sb))
                {
                    using (var xmlWriter = XmlWriter.Create(writer, WriterSettings))
                    {
                        while (reader.Read())
                        {
                            xmlWriter.WriteNode(reader, true);

                            if (reader.NodeType == XmlNodeType.EndElement)
                                break;
                        }
                    }
                }

                PersonContent.Value = sb.ToString();

                reader = XmlReader.Create(new StringReader(sb.ToString()), ReaderSettings);
            }
            catch (Exception e)
            {
                if (Plugin.Instance.DebugMode)
                {
                    Plugin.Instance.Logger.Debug(e.Message);
                    Plugin.Instance.Logger.Debug(e.StackTrace);
                }
            }

            return true;
        }

        [HarmonyPostfix]
        private static void GetPersonFromXmlNodePostfix(XmlReader reader, Task<PersonInfo> __result)
        {
            Task.Run(async () => await SetImageUrlAsync(__result)).ConfigureAwait(false);
        }

        private static async Task SetImageUrlAsync(Task<PersonInfo> personInfoTask)
        {
            try
            {
                var personInfo = await personInfoTask;

                var personContent = PersonContent.Value;
                PersonContent.Value = null;

                if (personContent != null)
                {
                    using (var reader = XmlReader.Create(new StringReader(personContent), ReaderSettings))
                    {
                        while (await reader.ReadAsync().ConfigureAwait(false))
                        {
                            if (reader.IsStartElement("thumb"))
                            {
                                var thumb = await reader.ReadElementContentAsStringAsync().ConfigureAwait(false);

                                if (IsValidHttpUrl(thumb))
                                {
                                    personInfo.ImageUrl = thumb;
                                    //Plugin.Instance.logger.Debug("EnhanceNfoMetadata - Imported " + personInfo.Name +
                                    //                             " " + personInfo.ImageUrl);
                                }

                                break;
                            }
                        }
                    }
                }
            }
            catch (Exception e)
            {
                if (Plugin.Instance.DebugMode)
                {
                    Plugin.Instance.Logger.Debug(e.Message);
                    Plugin.Instance.Logger.Debug(e.StackTrace);
                }
            }
        }
    }
}
