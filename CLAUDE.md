# Auto Project Instructions

## Quan hệ với DEV auto (D:\G\DEV)

* Đây là dự án mới (`D:\G\Auto`), tách ra từ `D:\G\DEV` để chuyển giao diện từ WinForms sang WPF. Logic không liên quan UI (Attack, Movement, Loot, Repair, Runtime, Support, Market, Sale, Utils, Native) được **copy nguyên** từ `D:\G\DEV` sang đây, không phải class library dùng chung.
* Vì là bản copy, không tự đồng bộ: khi sửa bug/tính năng ở logic không-UI trong `D:\G\DEV`, phải tự tay port lại thay đổi tương ứng sang đây (và ngược lại), không có cơ chế tự động.
* `D:\G\DEV` vẫn đang được maintain song song cho đến khi dự án này hoàn thiện UI đủ để thay thế hoàn toàn — không coi `D:\G\DEV` là đã đóng băng.
* Toàn bộ "Confirmed AutoFS Attack/Movement/Loot/Sale Flow", "AutoFS Port Verification Rules", "Memory and Reverse-Engineering Rules", "Game Automation Restrictions" trong `D:\G\DEV\AGENTS.md` vẫn áp dụng nguyên vẹn ở đây — hành vi game không đổi theo framework UI.
* `D:\G\DEV\Resource` vẫn là root bằng chứng AutoFS duy nhất; không tạo `Resource` riêng ở đây, tham chiếu ngược lại `D:\G\DEV\Resource` khi cần.

## Project Scope

Gọi ứng dụng của dự án này là `Auto` (phân biệt với `DEV auto` của `D:\G\DEV`) trong báo cáo, tài liệu, và thảo luận kỹ thuật.

## Technology

* C# với WPF (XAML) trên .NET 10.
* Namespace gốc: `Auto` (khác với `DEV` bên project cũ) — **giả định**, cần xác nhận lại nếu muốn đổi.
* UI viết bằng XAML (`.xaml` + code-behind `.xaml.cs`) — khác hẳn quy tắc "không Designer/.resx/XAML" của `D:\G\DEV\CLAUDE.md`, vì đó là rule dành riêng cho WinForms code-only.
* Style/Theme: dùng `ResourceDictionary`/`Style`/`ControlTemplate` tập trung, tương tự cách AutoFS gốc tổ chức (`Resource/AutoSource` có `themes/*.baml`, `styleresource.baml`) — không bắt buộc giống hệt, nhưng nên tránh style rải rác từng file XAML.

## Existing Project Structure (kế thừa từ DEV auto)

* `Attack`, `Movement`, `Loot`, `Repair`, `Runtime`, `Utils`, `Native` — copy nguyên từ `D:\G\DEV`, không chứa type UI.
* `UI` — viết lại hoàn toàn bằng XAML + code-behind, thay cho các file `UI/*.cs` code-only của bản WinForms.
* Không di chuyển trách nhiệm giữa các folder này trừ khi có lý do cụ thể liên quan task đang làm.

## C# Formatting và Indentation

* Áp dụng nguyên các quy tắc từ `d:\G\.claude\CLAUDE.md` (global): tab-only indentation, brace cùng dòng, format gọn.
* Áp dụng thêm cho `.xaml`: indent bằng tab (không dùng space), mỗi attribute dài nên xuống dòng riêng khi phần tử có nhiều thuộc tính, giữ nhất quán với style Visual Studio XAML formatter mặc định.

## Đồng bộ code không-UI

* Trước khi sửa 1 file trong `Attack/Movement/Loot/Repair/Runtime/Utils/Native` ở đây, kiểm tra xem file tương ứng bên `D:\G\DEV` có cùng nội dung không — nếu lệch, báo rõ cho chủ dự án biết đang sửa trên bản nào, không tự ý port ngược khi chưa được yêu cầu.

## Verification

* Không build/publish tạo `.exe` trừ khi được yêu cầu — theo đúng rule global.
* Khi cần kiểm tra XAML hợp lệ, có thể build project demo/scratchpad riêng thay vì build chính `Auto.csproj`, trừ khi chủ dự án yêu cầu build trực tiếp.

## Git Rules

* Không tự `git commit`/`git push` trừ khi được yêu cầu rõ ràng.

## Trạng thái hiện tại

* Folder mới tạo, chưa có file `.csproj`, chưa copy code, chưa có XAML nào. Đây là điểm khởi đầu, không phải trạng thái đã hoàn thiện.
