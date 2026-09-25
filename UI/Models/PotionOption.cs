namespace Auto.UI.Models;

// Một loại thuốc có thể mua nhanh. Code là mã chi tiết trong bộ ba (1, mã, 0) của vật phẩm, khớp bảng mã lệnh 95 của AutoFS.
public sealed record PotionOption(string Name, int Code);
