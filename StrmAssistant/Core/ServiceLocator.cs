using System;
using System.Collections.Concurrent;
using MediaBrowser.Model.Logging;

namespace StrmAssistant.Core
{
    /// <summary>
    /// 服务定位器，用于管理和访问全局服务实例
    /// 提供统一的服务注册和获取机制
    /// </summary>
    public class ServiceLocator
    {
        private static ServiceLocator _instance;
        private static readonly object _lock = new object();
        
        private readonly ConcurrentDictionary<Type, object> _services;
        private readonly ConcurrentDictionary<string, object> _namedServices;
        private readonly ILogger _logger;
        
        private ServiceLocator(ILogger logger)
        {
            _logger = logger;
            _services = new ConcurrentDictionary<Type, object>();
            _namedServices = new ConcurrentDictionary<string, object>();
            _logger.Debug("ServiceLocator initialized");
        }
        
        public static ServiceLocator Instance
        {
            get
            {
                if (_instance == null)
                {
                    throw new InvalidOperationException("ServiceLocator not initialized. Call Initialize() first.");
                }
                return _instance;
            }
        }
        
        public static void Initialize(ILogger logger)
        {
            if (_instance == null)
            {
                lock (_lock)
                {
                    if (_instance == null)
                    {
                        _instance = new ServiceLocator(logger);
                    }
                }
            }
        }
        
        /// <summary>
        /// 注册服务实例
        /// </summary>
        public void Register<T>(T service) where T : class
        {
            if (service == null)
                throw new ArgumentNullException(nameof(service));
            
            var type = typeof(T);
            if (_services.TryAdd(type, service))
            {
                _logger.Debug($"Service registered: {type.Name}");
            }
            else
            {
                _logger.Warn($"Service already registered: {type.Name}, replacing...");
                _services[type] = service;
            }
        }
        
        /// <summary>
        /// 注册命名服务实例
        /// </summary>
        public void RegisterNamed<T>(string name, T service) where T : class
        {
            if (string.IsNullOrEmpty(name))
                throw new ArgumentNullException(nameof(name));
            if (service == null)
                throw new ArgumentNullException(nameof(service));
            
            var key = $"{typeof(T).Name}:{name}";
            if (_namedServices.TryAdd(key, service))
            {
                _logger.Debug($"Named service registered: {key}");
            }
            else
            {
                _logger.Warn($"Named service already registered: {key}, replacing...");
                _namedServices[key] = service;
            }
        }
        
        /// <summary>
        /// 获取服务实例
        /// </summary>
        public T GetService<T>() where T : class
        {
            var type = typeof(T);
            if (_services.TryGetValue(type, out var service))
            {
                return service as T;
            }
            
            _logger.Warn($"Service not found: {type.Name}");
            return null;
        }
        
        /// <summary>
        /// 获取命名服务实例
        /// </summary>
        public T GetNamedService<T>(string name) where T : class
        {
            if (string.IsNullOrEmpty(name))
                throw new ArgumentNullException(nameof(name));
            
            var key = $"{typeof(T).Name}:{name}";
            if (_namedServices.TryGetValue(key, out var service))
            {
                return service as T;
            }
            
            _logger.Warn($"Named service not found: {key}");
            return null;
        }
        
        /// <summary>
        /// 尝试获取服务实例
        /// </summary>
        public bool TryGetService<T>(out T service) where T : class
        {
            service = GetService<T>();
            return service != null;
        }
        
        /// <summary>
        /// 检查服务是否已注册
        /// </summary>
        public bool IsRegistered<T>() where T : class
        {
            return _services.ContainsKey(typeof(T));
        }
        
        /// <summary>
        /// 注销服务
        /// </summary>
        public void Unregister<T>() where T : class
        {
            var type = typeof(T);
            if (_services.TryRemove(type, out _))
            {
                _logger.Debug($"Service unregistered: {type.Name}");
            }
        }
        
        /// <summary>
        /// 清空所有服务
        /// </summary>
        public void Clear()
        {
            _services.Clear();
            _namedServices.Clear();
            _logger.Debug("ServiceLocator cleared");
        }
    }
}
