using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using MediaBrowser.Model.Logging;

namespace StrmAssistant.Core
{
    /// <summary>
    /// Emby版本适配器，负责检测和管理不同版本之间的API差异
    /// </summary>
    public class EmbyVersionAdapter
    {
        private static EmbyVersionAdapter _instance;
        private static readonly object _lock = new object();
        
        private readonly ILogger _logger;
        private readonly Version _currentVersion;
        private readonly EmbyApiVersion _apiVersion;
        
        // 缓存反射获取的方法和类型
        private readonly ConcurrentDictionary<string, MethodInfo> _methodCache 
            = new ConcurrentDictionary<string, MethodInfo>();
        private readonly ConcurrentDictionary<string, Type> _typeCache 
            = new ConcurrentDictionary<string, Type>();
        private readonly ConcurrentDictionary<string, Assembly> _assemblyCache 
            = new ConcurrentDictionary<string, Assembly>();
        
        private EmbyVersionAdapter(ILogger logger, Version currentVersion)
        {
            _logger = logger;
            _currentVersion = currentVersion;
            _apiVersion = DetermineApiVersion(currentVersion);
            
            _logger.Info($"Emby Version Adapter initialized for version {currentVersion} (API Version: {_apiVersion})");
        }
        
        public static EmbyVersionAdapter Instance
        {
            get
            {
                if (_instance == null)
                {
                    throw new InvalidOperationException("EmbyVersionAdapter not initialized. Call Initialize() first.");
                }
                return _instance;
            }
        }
        
        public static void Initialize(ILogger logger, Version currentVersion)
        {
            if (_instance == null)
            {
                lock (_lock)
                {
                    if (_instance == null)
                    {
                        _instance = new EmbyVersionAdapter(logger, currentVersion);
                    }
                }
            }
        }
        
        public Version CurrentVersion => _currentVersion;
        public EmbyApiVersion ApiVersion => _apiVersion;
        
        /// <summary>
        /// 根据版本号确定API版本
        /// </summary>
        private EmbyApiVersion DetermineApiVersion(Version version)
        {
            // 从高到低检查版本，确保准确匹配
            if (version >= new Version("4.10.0.0"))
                return EmbyApiVersion.V4_10_0;
            if (version >= new Version("4.9.2.0"))
                return EmbyApiVersion.V4_9_2;
            if (version >= new Version("4.9.1.90"))
                return EmbyApiVersion.V4_9_1_90;
            if (version >= new Version("4.9.1.80"))
                return EmbyApiVersion.V4_9_1_80;
            if (version >= new Version("4.9.1.0"))
                return EmbyApiVersion.V4_9_1;
            if (version >= new Version("4.9.0.0"))
                return EmbyApiVersion.V4_9_0;
            if (version >= new Version("4.8.3.0"))
                return EmbyApiVersion.V4_8_3;
            if (version >= new Version("4.8.0.0"))
                return EmbyApiVersion.V4_8_0;
            
            return EmbyApiVersion.Unknown;
        }
        
        /// <summary>
        /// 检查当前版本是否支持某个功能
        /// </summary>
        public bool IsFeatureSupported(string featureName)
        {
            return featureName switch
            {
                "IntroSkip" => _apiVersion >= EmbyApiVersion.V4_8_0,
                "EpisodeGroups" => _apiVersion >= EmbyApiVersion.V4_9_0,
                "EnhancedMediaInfo" => _apiVersion >= EmbyApiVersion.V4_9_1_80,
                "AdvancedFingerprinting" => _apiVersion >= EmbyApiVersion.V4_9_1,
                "OptimizedMediaSources" => _apiVersion >= EmbyApiVersion.V4_9_1_90,
                "EnhancedNotifications" => _apiVersion >= EmbyApiVersion.V4_9_1_90,
                _ => false
            };
        }
        
        /// <summary>
        /// 检查当前版本是否至少满足最小版本要求
        /// </summary>
        public bool IsVersionAtLeast(EmbyApiVersion minimumVersion)
        {
            return _apiVersion >= minimumVersion;
        }
        
        /// <summary>
        /// 检查当前版本是否在指定范围内
        /// </summary>
        public bool IsVersionInRange(EmbyApiVersion minVersion, EmbyApiVersion maxVersion)
        {
            return _apiVersion >= minVersion && _apiVersion <= maxVersion;
        }
        
        /// <summary>
        /// 尝试加载程序集（带缓存）
        /// </summary>
        public Assembly TryLoadAssembly(string assemblyName)
        {
            return _assemblyCache.GetOrAdd(assemblyName, name =>
            {
                try
                {
                    return Assembly.Load(name);
                }
                catch (Exception ex)
                {
                    _logger.Warn($"Failed to load assembly '{name}': {ex.Message}");
                    return null;
                }
            });
        }
        
        /// <summary>
        /// 尝试获取类型（带缓存）
        /// </summary>
        public Type TryGetType(string assemblyName, string typeName)
        {
            var key = $"{assemblyName}::{typeName}";
            return _typeCache.GetOrAdd(key, _ =>
            {
                var assembly = TryLoadAssembly(assemblyName);
                if (assembly == null) return null;
                
                try
                {
                    return assembly.GetType(typeName);
                }
                catch (Exception ex)
                {
                    _logger.Warn($"Failed to get type '{typeName}' from assembly '{assemblyName}': {ex.Message}");
                    return null;
                }
            });
        }
        
        /// <summary>
        /// 查找兼容的方法（支持多个版本的方法签名）
        /// </summary>
        public MethodInfo FindCompatibleMethod(
            Type type, 
            string methodName, 
            BindingFlags bindingFlags, 
            params Type[][] parameterTypeVariants)
        {
            if (type == null || string.IsNullOrEmpty(methodName))
                return null;
            
            var cacheKey = $"{type.FullName}.{methodName}";
            
            return _methodCache.GetOrAdd(cacheKey, _ =>
            {
                foreach (var parameterTypes in parameterTypeVariants)
                {
                    try
                    {
                        var method = type.GetMethod(methodName, bindingFlags, null, parameterTypes, null);
                        if (method != null)
                        {
                            _logger.Debug(
                                $"Found compatible method: {type.Name}.{methodName} with {parameterTypes.Length} parameters");
                            return method;
                        }
                    }
                    catch (AmbiguousMatchException)
                    {
                        // 尝试更精确的匹配
                        var methods = type.GetMethods(bindingFlags)
                            .Where(m => m.Name == methodName && CheckMethodSignature(m, parameterTypes))
                            .ToArray();
                        
                        if (methods.Length == 1)
                        {
                            return methods[0];
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.Debug($"Method lookup failed for {methodName}: {ex.Message}");
                    }
                }
                
                _logger.Warn($"Could not find compatible method: {type.Name}.{methodName}");
                return null;
            });
        }
        
        /// <summary>
        /// 检查方法签名是否匹配
        /// </summary>
        private bool CheckMethodSignature(MethodInfo method, Type[] expectedParameterTypes)
        {
            if (method == null) return false;
            
            var parameters = method.GetParameters();
            if (parameters.Length != expectedParameterTypes.Length) return false;
            
            for (int i = 0; i < parameters.Length; i++)
            {
                if (parameters[i].ParameterType != expectedParameterTypes[i])
                {
                    return false;
                }
            }
            
            return true;
        }
        
        /// <summary>
        /// 创建安全的方法调用器（带异常处理，使用FastReflection提升性能）
        /// </summary>
        public Func<object, object[], object> CreateSafeMethodInvoker(MethodInfo method, string contextName)
        {
            if (method == null)
            {
                _logger.Warn($"Cannot create invoker for null method in {contextName}");
                return null;
            }
            
            // 尝试使用FastReflection创建高性能调用器
            try
            {
                if (FastReflection.Instance != null)
                {
                    var fastInvoker = FastReflection.Instance.CreateMethodInvoker(method);
                    if (fastInvoker != null)
                    {
                        return (instance, args) =>
                        {
                            try
                            {
                                return fastInvoker(instance, args);
                            }
                            catch (TargetInvocationException tie)
                            {
                                var innerEx = tie.InnerException ?? tie;
                                _logger.Error($"Fast invocation failed in {contextName}: {innerEx.Message}");
                                throw innerEx;
                            }
                            catch (Exception ex)
                            {
                                _logger.Error($"Unexpected error in {contextName}: {ex.Message}");
                                throw;
                            }
                        };
                    }
                }
            }
            catch
            {
                // FastReflection不可用，回退到标准反射
            }
            
            // 回退到标准反射
            return (instance, args) =>
            {
                try
                {
                    return method.Invoke(instance, args);
                }
                catch (TargetInvocationException tie)
                {
                    var innerEx = tie.InnerException ?? tie;
                    _logger.Error($"Method invocation failed in {contextName}: {innerEx.Message}");
                    throw innerEx;
                }
                catch (Exception ex)
                {
                    _logger.Error($"Unexpected error in {contextName}: {ex.Message}");
                    throw;
                }
            };
        }
        
        /// <summary>
        /// 记录版本兼容性信息
        /// </summary>
        public void LogCompatibilityInfo(string componentName, bool isCompatible, string details = null)
        {
            var status = isCompatible ? "✓" : "✗";
            var message = $"[{componentName}] {status} Compatible with Emby {_currentVersion}";
            
            if (!string.IsNullOrEmpty(details))
            {
                message += $" - {details}";
            }
            
            if (isCompatible)
            {
                _logger.Debug(message);
            }
            else
            {
                _logger.Warn(message);
            }
        }
        
        /// <summary>
        /// 清空缓存（用于测试或重新初始化）
        /// </summary>
        public void ClearCache()
        {
            _methodCache.Clear();
            _typeCache.Clear();
            _assemblyCache.Clear();
            _logger.Debug("EmbyVersionAdapter cache cleared");
        }
    }
}
