using System;
using System.Collections.Generic;

namespace SaveGameBackup.Core.Models;

/// <summary>
/// Kết quả phân trang tổng quát hỗ trợ cả SQL-level Pagination và RAM Pagination.
/// </summary>
/// <typeparam name="T">Kiểu dữ liệu của mỗi phần tử trong trang</typeparam>
public class PagedResult<T>
{
    public List<T> Items { get; set; } = new();
    public int TotalItems { get; set; }
    public int PageNumber { get; set; }
    public int PageSize { get; set; }
    public int TotalPages => PageSize > 0 ? Math.Max(1, (int)Math.Ceiling(TotalItems / (double)PageSize)) : 1;
    public bool HasPreviousPage => PageNumber > 1;
    public bool HasNextPage => PageNumber < TotalPages;
}
