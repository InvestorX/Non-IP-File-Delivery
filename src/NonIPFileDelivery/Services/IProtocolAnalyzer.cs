using System.Threading.Tasks;
using NonIPFileDelivery.Models;

namespace NonIPFileDelivery.Services
{
    /// <summary>
    /// プロトコル解析器インターフェース
    /// Phase 2実装: Strategy Patternによる拡張可能な解析基盤
    /// </summary>
    public interface IProtocolAnalyzer
    {
        /// <summary>
        /// パケットデータを解析してプロトコルを判定・解析する
        /// </summary>
        /// <param name="packetData">生パケットデータ</param>
        /// <returns>解析結果</returns>
        Task<ProtocolAnalysisResult> AnalyzeAsync(byte[] packetData);

        /// <summary>
        /// 指定されたポート番号を解析可能か判定
        /// </summary>
        /// <param name="port">ポート番号</param>
        /// <returns>解析可能な場合true</returns>
        bool CanAnalyze(int port);

        /// <summary>
        /// パケットデータからプロトコルタイプを検出（同期版）
        /// </summary>
        /// <param name="packetData">生パケットデータ</param>
        /// <returns>検出されたプロトコルタイプ</returns>
        ProtocolType DetectProtocol(byte[] packetData);

        /// <summary>
        /// パケットデータを解析（同期版）
        /// </summary>
        /// <param name="packetData">生パケットデータ</param>
        /// <param name="protocolType">プロトコルタイプ</param>
        /// <returns>解析結果</returns>
        ProtocolAnalysisResult Analyze(byte[] packetData, ProtocolType protocolType);
    }

    /// <summary>
    /// FTP解析器インターフェース
    /// </summary>
    public interface IFTPAnalyzer : IProtocolAnalyzer
    {
        /// <summary>
        /// FTPコマンドを解析
        /// </summary>
        /// <param name="data">FTPパケットデータ</param>
        /// <returns>FTP解析結果</returns>
        Task<FTPAnalysisResult> AnalyzeFTPAsync(byte[] data);
    }

    /// <summary>
    /// PostgreSQL解析器インターフェース
    /// </summary>
    public interface IPostgreSQLAnalyzer : IProtocolAnalyzer
    {
        /// <summary>
        /// PostgreSQLメッセージを解析
        /// </summary>
        /// <param name="data">PostgreSQLパケットデータ</param>
        /// <returns>PostgreSQL解析結果</returns>
        Task<PostgreSQLAnalysisResult> AnalyzePostgreSQLAsync(byte[] data);
    }
}