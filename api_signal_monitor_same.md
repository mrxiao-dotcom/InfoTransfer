# SignalMonitorController - same 接口文档

## 接口概述

获取策略信号指标的聚合数据，按品种分组展示各策略的信号状态、盈亏、风险等指标。
请求的地址是http://43.136.60.93:30090

## 请求

```
GET /ai-api/solutions/SignalMonitor/same
```

## 请求参数（Query String）

| 参数 | 类型 | 必填 | 默认值 | 说明 |
|------|------|------|--------|------|
| `Strategys` | `string[]` | 否 | `[]` | 筛选的策略名称列表。为空时不过滤策略，返回所有策略数据 |
| `ProductIds` | `string[]` | 否 | `[]` | 筛选的品种标识列表。为空时不过滤品种，返回所有品种数据 |

**请求示例：**

```
GET /ai-api/solutions/SignalMonitor/same?Strategys=GD15&Strategys=Momentum&ProductIds=rb&ProductIds=IF
```

## 响应

**HTTP 200** - 返回 JSON 对象：

```json
{
  "columns": ["GD15", "Momentum"],
  "data": [
    {
      "productId": "rb",
      "instrumentId": "rb2510",
      "long": 2,
      "short": 1,
      "none": 0,
      "lastPrice": 3560.0,
      "contractUnit": 10,
      "items": {
        "GD15": {
          "strategy": "GD15",
          "profitAndLoss": 1200.0,
          "rateProfitAndLoss": 0.05,
          "totalRateProfitAndLoss": 0.32,
          "historyRateProfitAndLoss": 0.15,
          "openPrice": 3500.0,
          "outPrice": 3400.0,
          "realTimeOutPrice": 3420.0,
          "stopPriceDiffRate": 0.0286,
          "totalStopPriceDiffRate": 0.12,
          "realTimeStopPriceDiffRate": 0.0229,
          "totalRealTimeStopPriceDiffRate": 0.10,
          "remainingLossAmount": -1600.0,
          "remainingRisk": 0.0449,
          "remainingTicks": 14,
          "latestMarketValue": 35600.0,
          "isTodayChange": true,
          "direction": "Long",
          "changeType": "Signal",
          "tickTime": "2025-05-03 14:30:00"
        }
      }
    }
  ]
}
```

## 响应字段说明

### 顶层字段

| 字段 | 类型 | 说明 |
|------|------|------|
| `columns` | `string[]` | 表格列标题策略列表。当请求中 `Strategys` 非空时使用请求值，否则返回系统中所有可用策略名称 |
| `data` | `SignalMetricAggregation[]` | 按品种聚合的信号指标数据列表 |

### SignalMetricAggregation（品种聚合）

| 字段 | 类型 | 说明 |
|------|------|------|
| `productId` | `string` | 品种标识，如 `rb`、`IF` |
| `instrumentId` | `string` | 聚合结果选定代表的合约标识，如 `rb2510` |
| `long` | `int` | 当前品种下做多方向的策略数量 |
| `short` | `int` | 当前品种下做空方向的策略数量 |
| `none` | `int` | 当前品种下空仓方向的策略数量 |
| `lastPrice` | `decimal` | 代表合约的最新价 |
| `contractUnit` | `int` | 合约交易单位（每手数量） |
| `items` | `object` | 各策略指标明细，key 为策略名称，value 为 `SignalMetricItem` |

### SignalMetricItem（策略指标明细）

| 字段 | 类型 | 说明 |
|------|------|------|
| `strategy` | `string` | 策略名称 |
| `profitAndLoss` | `decimal` | 基于最新价计算的盈亏金额 |
| `rateProfitAndLoss` | `decimal` | 基于最新价计算的收益率（小数，如 0.05 表示 5%） |
| `totalRateProfitAndLoss` | `decimal` | 历史累计总收益率 |
| `historyRateProfitAndLoss` | `decimal` | 历史版本信号链累计的历史收益率 |
| `openPrice` | `decimal` | 当前信号口径下的开仓价 |
| `outPrice` | `decimal` | 当前信号的止损价 |
| `realTimeOutPrice` | `decimal` | 当前信号的实时止损价 |
| `stopPriceDiffRate` | `decimal` | 当前信号的止损价差比例 |
| `totalStopPriceDiffRate` | `decimal` | 历史累计总止损价差比例 |
| `realTimeStopPriceDiffRate` | `decimal` | 当前信号的实时止损价差比例 |
| `totalRealTimeStopPriceDiffRate` | `decimal` | 历史累计总实时止损价差比例 |
| `remainingLossAmount` | `decimal` | 从当前价到止损价剩余的亏损金额（负数表示可能亏损） |
| `remainingRisk` | `decimal` | 剩余风险比例 |
| `remainingTicks` | `decimal` | 从当前价到止损价剩余的最小跳动数 |
| `latestMarketValue` | `decimal` | 按最新价计算的最新市值 |
| `isTodayChange` | `boolean` | 当前信号是否属于当日变动 |
| `direction` | `string` | 信号方向，见枚举值参考 |
| `changeType` | `string` | 信号变化类型，见枚举值参考 |
| `tickTime` | `string` | 当前策略信号的时间戳文本 |

## 枚举值参考

### SignalDirection（信号方向）

| 返回值 | 说明 |
|--------|------|
| `"None"` | 无仓 |
| `"Long"` | 多头 |
| `"Short"` | 空头 |

### SignalChangeType（信号变化类型）

| 返回值 | 说明 |
|--------|------|
| `"Signal"` | 信号变化（方向/合约变更） |
| `"Change"` | 换月（合约换月） |

## 业务说明

1. **columns 的生成逻辑**：如果请求传入 `Strategys` 参数，则 `columns` 直接使用请求值；否则从系统中获取所有可用策略名称，按字母排序返回。
2. **聚合逻辑**：返回数据按品种（ProductId）聚合，同一品种下所有策略的指标归入同一 `SignalMetricAggregation`。
3. **items 的 key**：`items` 是一个字典，key 为策略名称（不区分大小写），value 为该策略在该品种下的指标明细。
4. **收益率字段**：`rateProfitAndLoss` 是当前单次信号的收益率，`totalRateProfitAndLoss` 是历史累计总收益率，`historyRateProfitAndLoss` 是历史版本信号链的累计收益率。
5. **风险相关字段**：`remainingLossAmount`、`remainingRisk`、`remainingTicks` 三个字段从不同维度衡量当前持仓到止损价的距离风险。
