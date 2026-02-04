using System;
using System.Collections.Concurrent;
using System.Linq.Expressions;
using System.Reflection;
using MediaBrowser.Model.Logging;

namespace StrmAssistant.Core
{
    /// <summary>
    /// 高性能反射调用系统
    /// 使用编译的Expression树将反射调用转换为委托，性能接近直接调用
    /// </summary>
    public class FastReflection
    {
        private static FastReflection _instance;
        private static readonly object _lock = new object();
        
        private readonly ILogger _logger;
        private readonly ConcurrentDictionary<string, Delegate> _methodDelegateCache;
        private readonly ConcurrentDictionary<string, Func<object, object>> _propertyGetterCache;
        private readonly ConcurrentDictionary<string, Action<object, object>> _propertySetterCache;
        
        // 性能统计
        private long _cacheHits;
        private long _cacheMisses;
        private long _totalInvocations;
        
        private FastReflection(ILogger logger)
        {
            _logger = logger;
            _methodDelegateCache = new ConcurrentDictionary<string, Delegate>();
            _propertyGetterCache = new ConcurrentDictionary<string, Func<object, object>>();
            _propertySetterCache = new ConcurrentDictionary<string, Action<object, object>>();
            
            _logger.Debug("FastReflection initialized");
        }
        
        public static FastReflection Instance
        {
            get
            {
                if (_instance == null)
                {
                    throw new InvalidOperationException("FastReflection not initialized. Call Initialize() first.");
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
                        _instance = new FastReflection(logger);
                    }
                }
            }
        }
        
        /// <summary>
        /// 创建高性能方法调用器（返回泛型委托）
        /// </summary>
        public Func<object, object[], object> CreateMethodInvoker(MethodInfo method)
        {
            if (method == null) return null;
            
            var key = GetMethodKey(method);
            
            var cachedDelegate = _methodDelegateCache.GetOrAdd(key, _ =>
            {
                try
                {
                    // 创建参数表达式
                    var instanceParam = Expression.Parameter(typeof(object), "instance");
                    var argsParam = Expression.Parameter(typeof(object[]), "args");
                    
                    // 将参数转换为正确的类型
                    var parameters = method.GetParameters();
                    var argExpressions = new Expression[parameters.Length];
                    
                    for (int i = 0; i < parameters.Length; i++)
                    {
                        var argAccess = Expression.ArrayIndex(argsParam, Expression.Constant(i));
                        argExpressions[i] = Expression.Convert(argAccess, parameters[i].ParameterType);
                    }
                    
                    // 创建方法调用表达式
                    Expression callExpression;
                    if (method.IsStatic)
                    {
                        callExpression = Expression.Call(method, argExpressions);
                    }
                    else
                    {
                        var instanceExpression = Expression.Convert(instanceParam, method.DeclaringType);
                        callExpression = Expression.Call(instanceExpression, method, argExpressions);
                    }
                    
                    // 处理返回值
                    if (method.ReturnType == typeof(void))
                    {
                        var voidLambda = Expression.Lambda<Action<object, object[]>>(
                            callExpression, instanceParam, argsParam);
                        var compiledAction = voidLambda.Compile();
                        
                        // 包装为Func返回null
                        return new Func<object, object[], object>((inst, args) =>
                        {
                            compiledAction(inst, args);
                            return null;
                        });
                    }
                    else
                    {
                        var convertExpression = Expression.Convert(callExpression, typeof(object));
                        var lambda = Expression.Lambda<Func<object, object[], object>>(
                            convertExpression, instanceParam, argsParam);
                        return lambda.Compile();
                    }
                }
                catch (Exception ex)
                {
                    _logger.Warn($"Failed to compile method invoker for {method.Name}: {ex.Message}");
                    return null;
                }
            });
            
            return cachedDelegate as Func<object, object[], object>;
        }
        
        /// <summary>
        /// 快速调用方法（使用编译的委托）
        /// </summary>
        public object FastInvoke(MethodInfo method, object instance, params object[] arguments)
        {
            if (method == null)
                throw new ArgumentNullException(nameof(method));
            
            System.Threading.Interlocked.Increment(ref _totalInvocations);
            
            var invoker = CreateMethodInvoker(method);
            if (invoker != null)
            {
                System.Threading.Interlocked.Increment(ref _cacheHits);
                try
                {
                    return invoker(instance, arguments);
                }
                catch (Exception ex)
                {
                    _logger.Error($"Fast invocation failed for {method.Name}: {ex.Message}");
                    throw;
                }
            }
            else
            {
                // 回退到标准反射
                System.Threading.Interlocked.Increment(ref _cacheMisses);
                return method.Invoke(instance, arguments);
            }
        }
        
        /// <summary>
        /// 创建属性Getter（编译的委托）
        /// </summary>
        public Func<object, object> CreatePropertyGetter(PropertyInfo property)
        {
            if (property == null || !property.CanRead) return null;
            
            var key = GetPropertyKey(property);
            
            return _propertyGetterCache.GetOrAdd(key, _ =>
            {
                try
                {
                    var instanceParam = Expression.Parameter(typeof(object), "instance");
                    var instanceExpression = Expression.Convert(instanceParam, property.DeclaringType);
                    var propertyExpression = Expression.Property(instanceExpression, property);
                    var convertExpression = Expression.Convert(propertyExpression, typeof(object));
                    
                    var lambda = Expression.Lambda<Func<object, object>>(convertExpression, instanceParam);
                    return lambda.Compile();
                }
                catch (Exception ex)
                {
                    _logger.Warn($"Failed to compile property getter for {property.Name}: {ex.Message}");
                    return null;
                }
            });
        }
        
        /// <summary>
        /// 创建属性Setter（编译的委托）
        /// </summary>
        public Action<object, object> CreatePropertySetter(PropertyInfo property)
        {
            if (property == null || !property.CanWrite) return null;
            
            var key = GetPropertyKey(property);
            
            return _propertySetterCache.GetOrAdd(key, _ =>
            {
                try
                {
                    var instanceParam = Expression.Parameter(typeof(object), "instance");
                    var valueParam = Expression.Parameter(typeof(object), "value");
                    
                    var instanceExpression = Expression.Convert(instanceParam, property.DeclaringType);
                    var valueExpression = Expression.Convert(valueParam, property.PropertyType);
                    var propertyExpression = Expression.Property(instanceExpression, property);
                    var assignExpression = Expression.Assign(propertyExpression, valueExpression);
                    
                    var lambda = Expression.Lambda<Action<object, object>>(
                        assignExpression, instanceParam, valueParam);
                    return lambda.Compile();
                }
                catch (Exception ex)
                {
                    _logger.Warn($"Failed to compile property setter for {property.Name}: {ex.Message}");
                    return null;
                }
            });
        }
        
        /// <summary>
        /// 快速获取属性值
        /// </summary>
        public object FastGetProperty(PropertyInfo property, object instance)
        {
            var getter = CreatePropertyGetter(property);
            if (getter != null)
            {
                try
                {
                    return getter(instance);
                }
                catch (Exception ex)
                {
                    _logger.Error($"Fast property get failed for {property.Name}: {ex.Message}");
                    throw;
                }
            }
            else
            {
                return property.GetValue(instance);
            }
        }
        
        /// <summary>
        /// 快速设置属性值
        /// </summary>
        public void FastSetProperty(PropertyInfo property, object instance, object value)
        {
            var setter = CreatePropertySetter(property);
            if (setter != null)
            {
                try
                {
                    setter(instance, value);
                }
                catch (Exception ex)
                {
                    _logger.Error($"Fast property set failed for {property.Name}: {ex.Message}");
                    throw;
                }
            }
            else
            {
                property.SetValue(instance, value);
            }
        }
        
        /// <summary>
        /// 获取方法的唯一键
        /// </summary>
        private string GetMethodKey(MethodInfo method)
        {
            var paramTypes = string.Join(",", Array.ConvertAll(
                method.GetParameters(), p => p.ParameterType.FullName));
            return $"{method.DeclaringType?.FullName}.{method.Name}({paramTypes})";
        }
        
        /// <summary>
        /// 获取属性的唯一键
        /// </summary>
        private string GetPropertyKey(PropertyInfo property)
        {
            return $"{property.DeclaringType?.FullName}.{property.Name}";
        }
        
        /// <summary>
        /// 获取性能统计信息
        /// </summary>
        public PerformanceStats GetPerformanceStats()
        {
            return new PerformanceStats
            {
                TotalInvocations = _totalInvocations,
                CacheHits = _cacheHits,
                CacheMisses = _cacheMisses,
                CacheHitRate = _totalInvocations > 0 
                    ? (double)_cacheHits / _totalInvocations * 100 
                    : 0,
                MethodCacheSize = _methodDelegateCache.Count,
                PropertyGetterCacheSize = _propertyGetterCache.Count,
                PropertySetterCacheSize = _propertySetterCache.Count
            };
        }
        
        /// <summary>
        /// 清空所有缓存
        /// </summary>
        public void ClearCache()
        {
            _methodDelegateCache.Clear();
            _propertyGetterCache.Clear();
            _propertySetterCache.Clear();
            _cacheHits = 0;
            _cacheMisses = 0;
            _totalInvocations = 0;
            _logger.Debug("FastReflection cache cleared");
        }
        
        /// <summary>
        /// 预热常用方法（编译委托）
        /// </summary>
        public void WarmUp(params MethodInfo[] methods)
        {
            foreach (var method in methods)
            {
                if (method != null)
                {
                    CreateMethodInvoker(method);
                }
            }
            _logger.Debug($"FastReflection warmed up with {methods.Length} methods");
        }
    }
    
    /// <summary>
    /// 性能统计数据
    /// </summary>
    public class PerformanceStats
    {
        public long TotalInvocations { get; set; }
        public long CacheHits { get; set; }
        public long CacheMisses { get; set; }
        public double CacheHitRate { get; set; }
        public int MethodCacheSize { get; set; }
        public int PropertyGetterCacheSize { get; set; }
        public int PropertySetterCacheSize { get; set; }
        
        public override string ToString()
        {
            return $"FastReflection Stats: " +
                   $"Invocations={TotalInvocations}, " +
                   $"Hits={CacheHits}, " +
                   $"Misses={CacheMisses}, " +
                   $"HitRate={CacheHitRate:F2}%, " +
                   $"CacheSize={MethodCacheSize}";
        }
    }
}
