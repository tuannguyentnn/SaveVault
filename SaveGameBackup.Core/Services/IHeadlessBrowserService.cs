using System.Threading;
using System.Threading.Tasks;

namespace SaveGameBackup.Core.Services;

/// <summary>
/// Giao diện dịch vụ trình duyệt Headless (Chromium / WebView2)
/// Dùng để thực thi JavaScript và vượt qua các thử thách Cloudflare Managed Challenge khi cần thiết.
/// </summary>
public interface IHeadlessBrowserService
{
    /// <summary>
    /// Cho biết dịch vụ trình duyệt ngầm có sẵn sàng hoạt động trên nền tảng hiện tại hay không.
    /// </summary>
    bool IsAvailable { get; }

    /// <summary>
    /// Tải nội dung trang web hoặc JSON API sau khi trình duyệt đã giải quyết xong Cloudflare Challenge.
    /// </summary>
    /// <param name="url">URL cần truy vấn</param>
    /// <param name="timeoutSeconds">Thời gian chờ tối đa (mặc định 6 giây)</param>
    /// <param name="cancellationToken">Token hủy tác vụ</param>
    /// <returns>Nội dung chuỗi JSON hoặc HTML trích xuất từ trang</returns>
    Task<string?> FetchPageContentAsync(string url, int timeoutSeconds = 6, CancellationToken cancellationToken = default);
}
