using Ctp.Net;
using Aether.Exchange.Rpc;
using Aether.Exchange;
using Aether.Ledger;

// 全局命名空间（host 惯例——Program.cs / tests 共用）

/// <summary>
/// 提币 wire 处理器包装（批 4 接线，2026-10-08）：把 <see cref="LedgerBridge.WithdrawAsync"/> 包成
/// <see cref="SimWithdrawHandler"/> 契约（Sim.Server 不引 Aether.Ledger——层化）。
/// 链侧业务错（<see cref="LedgerClientException"/>）→ <see cref="ExchangeBusinessException"/>(90019, 消息透传)。
/// Program.cs 与集成测试共用这一份映射。
/// </summary>
public static class LedgerWithdrawGlue
{
    public static SimWithdrawHandler Wrap(LedgerBridge bridge)
        => async (account, toHex, atoms, asset) =>
        {
            try
            {
                var assetId = asset switch
                {
                    "coin" => LedgerSupply.Assets.Coin,
                    "quote" => LedgerSupply.Assets.Quote,
                    _ => throw new ExchangeBusinessException(SimErrorCodes.InvalidParameter, $"未知资产 \"{asset}\"（coin|quote）"),
                };
                return await bridge.WithdrawAsync(account, LedgerAddress.Parse(toHex), atoms, assetId);
            }
            catch (LedgerClientException ex)
            {
                throw new ExchangeBusinessException(SimErrorCodes.WithdrawalFailed, ex.Message);
            }
            catch (FormatException ex)
            {
                throw new ExchangeBusinessException(SimErrorCodes.InvalidParameter, $"提币地址非法：{ex.Message}");
            }
        };
}
