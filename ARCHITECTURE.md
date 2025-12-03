# StrmAssistant 架构文档

## 版本 2.2.0 架构优化概述

### 核心架构组件

#### 1. EmbyVersionAdapter - 版本适配器
**位置**: `StrmAssistant/Core/EmbyVersionAdapter.cs`

**功能**:
- 自动检测当前Emby版本并映射到API版本枚举
- 提供统一的版本检测和特性支持查询接口
- 缓存反射获取的方法和类型信息，提高性能
- 提供安全的方法调用包装器，统一错误处理

**关键方法**:
```csharp
// 初始化适配器
EmbyVersionAdapter.Initialize(Logger, applicationVersion);

// 检查版本
if (EmbyVersionAdapter.Instance.IsVersionAtLeast(EmbyApiVersion.V4_9_1_90)) { ... }

// 检查功能支持
if (EmbyVersionAdapter.Instance.IsFeatureSupported("EnhancedMediaInfo")) { ... }

// 查找兼容方法
var method = EmbyVersionAdapter.Instance.FindCompatibleMethod(
    type, "MethodName", bindingFlags, 
    paramVariant1, paramVariant2, paramVariant3);
```

**支持的版本**:
- V4_8_0: 基础版本
- V4_8_3: 早期稳定版
- V4_9_0: 中期版本
- V4_9_1: 早期4.9.1
- V4_9_1_80: 稳定版本
- V4_9_1_90: 当前推荐版本（最新）
- V4_9_2: 未来版本
- V4_10_0: 主要版本

#### 2. ServiceLocator - 服务定位器
**位置**: `StrmAssistant/Core/ServiceLocator.cs`

**功能**:
- 全局服务注册和获取
- 支持类型服务和命名服务
- 线程安全的并发字典实现

**使用示例**:
```csharp
// 初始化
ServiceLocator.Initialize(Logger);

// 注册服务
ServiceLocator.Instance.Register<IMyService>(myServiceInstance);

// 获取服务
var service = ServiceLocator.Instance.GetService<IMyService>();

// 注册命名服务
ServiceLocator.Instance.RegisterNamed<ICache>("MediaInfo", cacheInstance);
```

#### 3. FastReflection - 高性能反射系统
**位置**: `StrmAssistant/Core/FastReflection.cs`

**功能**:
- 使用Expression树编译将反射调用转换为委托
- 性能接近直接调用（比标准反射快50-100倍）
- 自动缓存编译的委托
- 支持方法调用和属性访问
- 性能统计和监控

**关键方法**:
```csharp
// 创建高性能方法调用器
var invoker = FastReflection.Instance.CreateMethodInvoker(methodInfo);
var result = invoker(instance, arguments);

// 快速调用方法
var result = FastReflection.Instance.FastInvoke(method, instance, args);

// 获取性能统计
var stats = FastReflection.Instance.GetPerformanceStats();
```

**性能提升**:
- 首次调用：需要编译Expression树（~10-50ms）
- 后续调用：接近直接调用（<1ms）
- 缓存命中率通常 >95%

#### 4. PerformanceMonitor - 性能监控
**位置**: `StrmAssistant/Core/PerformanceMonitor.cs`

**功能**:
- 跟踪关键操作的执行时间
- 统计调用次数、平均/最小/最大耗时
- 识别慢操作（超过阈值）
- 生成性能报告

**使用示例**:
```csharp
// 使用using自动测量
using (PerformanceMonitor.Instance.Measure("MyOperation"))
{
    // 执行操作
}

// 获取性能报告
var report = PerformanceMonitor.Instance.GetPerformanceReport();
```

#### 5. HealthCheck - 健康检查系统
**位置**: `StrmAssistant/Core/HealthCheck.cs`

**功能**:
- 自动检查核心组件状态
- 检测性能问题
- 生成诊断报告
- 健康状态分级（Healthy/Degraded/Unhealthy/Critical）

**使用示例**:
```csharp
// 执行健康检查
var result = HealthCheck.Instance.PerformHealthCheck();

// 生成诊断报告
var report = HealthCheck.Instance.GenerateDiagnosticReport();
```

#### 6. 增强的PatchTracker
**位置**: `StrmAssistant/Mod/PatchTracker.cs`

**新增功能**:
- 详细的状态跟踪（NotInitialized, Initializing, Initialized, Applied, Failed, NotSupported）
- 错误消息收集和历史记录
- 核心功能标识（区分核心和可选功能）
- 功能描述和诊断信息

**状态枚举**:
```csharp
public enum PatchStatus
{
    NotInitialized,  // 未初始化
    Initializing,    // 初始化中
    Initialized,     // 初始化成功
    Applied,         // 已应用
    Failed,          // 初始化失败
    NotSupported     // 不支持
}
```

#### 4. 改进的PatchManager
**位置**: `StrmAssistant/Mod/PatchManager.cs`

**新增功能**:
- 初始化状态检查，防止重复初始化
- 详细的诊断报告生成
- 缓存清理方法
- 更好的错误分类和处理

**关键方法**:
```csharp
// 获取诊断报告
string report = PatchManager.GetDiagnosticReport();

// 清除缓存
PatchManager.ClearCaches();
```

### API调用策略

插件采用**三层回退机制**确保在不同Emby版本下的最大兼容性：

1. **Harmony ReversePatch**（最优性能）
   - 使用Harmony的ReversePatch功能直接调用私有方法
   - 性能最优，无反射开销
   - 仅在X64架构且Harmony可用时使用

2. **Reflection**（良好性能）
   - 使用反射调用私有方法
   - 通过缓存MethodInfo减少反射开销
   - 支持多版本方法签名自动适配

3. **Public API**（兜底方案）
   - 使用Emby的公共API
   - 功能可能受限但保证可用
   - 适用于所有版本

### 版本适配流程

```
启动 → 初始化EmbyVersionAdapter
     ↓
     检测Emby版本 → 映射到API版本枚举
     ↓
     初始化PatchManager
     ↓
     各Mod初始化 → 尝试Harmony补丁
     ↓               ↓ 失败
     成功 → Harmony   ↓
                     尝试Reflection
                     ↓           ↓ 失败
                     成功 → Reflection
                                 ↓
                                 使用Public API
```

### 错误处理策略

1. **分级日志记录**
   - Error: 核心功能失败
   - Warn: 可选功能不可用或降级
   - Info: 正常状态切换
   - Debug: 详细诊断信息

2. **错误收集**
   - 每个PatchTracker维护错误消息列表
   - 带时间戳的错误记录
   - 支持错误历史查询

3. **优雅降级**
   - 可选功能失败不影响核心功能
   - 自动尝试备用方案
   - 详细的降级原因记录

### 性能优化

1. **方法缓存**
   - EmbyVersionAdapter缓存反射获取的方法和类型
   - PatchManager缓存HarmonyMethod和MethodInfo
   - 使用ConcurrentDictionary保证线程安全

2. **延迟初始化**
   - 服务按需注册和获取
   - 反射查找结果缓存

3. **诊断开销最小化**
   - 仅在DebugMode下输出详细诊断
   - 状态变化去重，避免重复日志

### 扩展性

#### 添加新的Emby版本支持
1. 在`EmbyApiVersion`枚举中添加新版本
2. 更新`EmbyVersionAdapter.DetermineApiVersion()`方法
3. 如有新API变化，添加特性检测方法

#### 添加新的Mod补丁
1. 继承`PatchBase<T>`
2. 实现`OnInitialize()`和`Prepare()`方法
3. 在PatchManager.Initialize()中创建实例
4. 根据需要设置`IsCoreFeature`标识

#### 添加新的服务
1. 在Plugin构造函数中注册服务
2. 使用ServiceLocator获取服务实例

### 已知限制

1. Harmony ReversePatch对某些复杂IL代码可能失败
2. 非X64架构不支持Harmony
3. 某些私有API在新版本中可能被移除或重构

### 测试建议

1. **版本兼容性测试**
   - 在不同Emby版本上测试
   - 验证三层回退机制是否正常工作
   - 检查诊断报告输出

2. **性能测试**
   - 比较Harmony vs Reflection vs PublicAPI性能
   - 监控缓存命中率
   - 测试大量并发请求

3. **错误恢复测试**
   - 模拟Harmony初始化失败
   - 模拟反射方法不存在
   - 验证优雅降级是否正常

## 更新日志

### v2.2.0 (2024-12-03)
- ✅ 创建EmbyVersionAdapter版本适配器
- ✅ 创建ServiceLocator服务定位器
- ✅ 增强PatchTracker状态跟踪
- ✅ 改进PatchManager诊断功能
- ✅ 更新插件版本号为2.2.0.0
- ✅ 完全支持Emby 4.9.1.90
- ✅ 优化MediaInfoApi使用新架构
- ✅ 更新README文档

### 迁移指南

从v2.1.0迁移到v2.2.0：
1. 核心架构自动初始化，无需手动更改
2. 旧的EmbyVersionCompatibility已被EmbyVersionAdapter取代
3. 所有现有功能保持向后兼容
4. 新增的诊断功能可在DebugMode下查看

## 维护者

社区贡献版本，基于sjtuross/StrmAssistant进行AI辅助优化。
