using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using StrmAssistant.Core;

namespace StrmAssistant.Common
{
    /// <summary>
    /// Emby版本兼容性检测工具类（兼容性包装器）
    /// 注意：此类已被EmbyVersionAdapter取代，保留仅为向后兼容
    /// 建议使用 StrmAssistant.Core.EmbyVersionAdapter 代替
    /// </summary>
    [Obsolete("使用 StrmAssistant.Core.EmbyVersionAdapter 代替")]
    public static class EmbyVersionCompatibility
    {
        // 已知的Emby版本里程碑
        public static readonly Version Version4800 = new Version("4.8.0.0");
        public static readonly Version Version4830 = new Version("4.8.3.0");
        public static readonly Version Version4900 = new Version("4.9.0.0");
        public static readonly Version Version4910 = new Version("4.9.1.0");
        public static readonly Version Version49180 = new Version("4.9.1.80");
        public static readonly Version Version49190 = new Version("4.9.1.90");
        
        /// <summary>
        /// 当前版本（兼容性包装）
        /// </summary>
        public static Version CurrentVersion => EmbyVersionAdapter.Instance.CurrentVersion;

        /// <summary>
        /// 检查方法签名是否匹配
        /// </summary>
        public static bool CheckMethodSignature(MethodInfo method, Type[] expectedParameterTypes)
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
        /// 尝试查找兼容的方法重载（委托到EmbyVersionAdapter）
        /// 支持多个版本的方法签名
        /// </summary>
        public static MethodInfo FindCompatibleMethod(Type type, string methodName, 
            BindingFlags bindingFlags, params Type[][] parameterTypeVariants)
        {
            return EmbyVersionAdapter.Instance.FindCompatibleMethod(type, methodName, bindingFlags, parameterTypeVariants);
        }

        /// <summary>
        /// 获取方法的安全调用包装器（委托到EmbyVersionAdapter）
        /// 提供异常处理和日志记录
        /// </summary>
        public static Func<object, object[], object> CreateSafeMethodInvoker(MethodInfo method, string contextName)
        {
            return EmbyVersionAdapter.Instance.CreateSafeMethodInvoker(method, contextName);
        }

        /// <summary>
        /// 检查类型是否存在指定的成员
        /// </summary>
        public static bool HasMember(Type type, string memberName, MemberTypes memberType)
        {
            if (type == null || string.IsNullOrEmpty(memberName))
                return false;

            try
            {
                var members = type.GetMember(memberName, memberType, 
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);
                return members.Length > 0;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 获取属性的安全访问器
        /// </summary>
        public static Func<object, object> CreateSafePropertyGetter(PropertyInfo property, string contextName)
        {
            if (property == null || !property.CanRead)
            {
                Plugin.Instance.Logger.Warn($"Cannot create getter for property in {contextName}");
                return null;
            }

            var getter = property.GetGetMethod(true);
            if (getter == null) return null;

            return (instance) =>
            {
                try
                {
                    return getter.Invoke(instance, null);
                }
                catch (Exception ex)
                {
                    Plugin.Instance.Logger.Error($"Property getter failed in {contextName}: {ex.Message}");
                    if (Plugin.Instance.DebugMode)
                    {
                        Plugin.Instance.Logger.Debug($"Property: {property.DeclaringType?.Name}.{property.Name}");
                        Plugin.Instance.Logger.Debug(ex.StackTrace);
                    }
                    throw;
                }
            };
        }

        /// <summary>
        /// 获取属性的安全设置器
        /// </summary>
        public static Action<object, object> CreateSafePropertySetter(PropertyInfo property, string contextName)
        {
            if (property == null || !property.CanWrite)
            {
                Plugin.Instance.Logger.Warn($"Cannot create setter for property in {contextName}");
                return null;
            }

            var setter = property.GetSetMethod(true);
            if (setter == null) return null;

            return (instance, value) =>
            {
                try
                {
                    setter.Invoke(instance, new[] { value });
                }
                catch (Exception ex)
                {
                    Plugin.Instance.Logger.Error($"Property setter failed in {contextName}: {ex.Message}");
                    if (Plugin.Instance.DebugMode)
                    {
                        Plugin.Instance.Logger.Debug($"Property: {property.DeclaringType?.Name}.{property.Name}");
                        Plugin.Instance.Logger.Debug($"Value type: {value?.GetType().Name ?? "null"}");
                        Plugin.Instance.Logger.Debug(ex.StackTrace);
                    }
                    throw;
                }
            };
        }

        /// <summary>
        /// 记录版本兼容性诊断信息（委托到EmbyVersionAdapter）
        /// </summary>
        public static void LogCompatibilityInfo(string componentName, bool isCompatible, string details = null)
        {
            EmbyVersionAdapter.Instance.LogCompatibilityInfo(componentName, isCompatible, details);
        }

        /// <summary>
        /// 尝试加载程序集（委托到EmbyVersionAdapter）
        /// </summary>
        public static Assembly TryLoadAssembly(string assemblyName)
        {
            return EmbyVersionAdapter.Instance.TryLoadAssembly(assemblyName);
        }

        /// <summary>
        /// 尝试获取类型（委托到EmbyVersionAdapter）
        /// </summary>
        public static Type TryGetType(Assembly assembly, string typeName)
        {
            return EmbyVersionAdapter.Instance.TryGetType(assembly?.GetName().Name, typeName);
        }
    }
}

