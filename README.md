<p align="center">
  <img src="assets/logo.png" width="96" alt="Desko logo">
</p>

<h1 align="center">Desko</h1>

<p align="center">
  <b>Sắp xếp desktop Windows gọn gàng với các fence.</b><br>
  Khung kính mờ gom icon theo nhóm, thu gọn, gộp thành tab, tìm nhanh, ẩn/hiện chỉ bằng một cú nhấp đúp.
</p>

<p align="center">
  <a href="https://github.com/doson709/Desko/actions/workflows/build.yml"><img src="https://github.com/doson709/Desko/actions/workflows/build.yml/badge.svg" alt="Build"></a>
  <a href="LICENSE"><img src="https://img.shields.io/badge/license-MIT-blue.svg" alt="MIT License"></a>
  <img src="https://img.shields.io/badge/Windows-10%20%7C%2011-0078D4.svg" alt="Windows 10 | 11">
  <img src="https://img.shields.io/badge/.NET-8-512BD4.svg" alt=".NET 8">
</p>

<p align="center">
  <a href="#cài-đặt-người-dùng">Cài đặt</a> ·
  <a href="#tính-năng">Tính năng</a> ·
  <a href="#phím-tắt">Phím tắt</a> ·
  <a href="#build-từ-mã-nguồn">Build</a>
</p>

*Desko organizes the Windows desktop into fences: frosted panels that group icons, roll up, merge into tabs and hide with a double-click. Windows 10/11, C# / WPF on .NET 8. The interface is in Vietnamese.*

> **Fence chứa chính icon của desktop.** File không bị di chuyển, ẩn hay xóa: File Explorer, hộp thoại mở / lưu file và trang web khi tải file lên đều thấy đầy đủ thư mục Desktop. Mở, chọn, đổi tên, kéo thả, menu chuột phải trong fence đều là của Windows.

## Tính năng

**Fence**
- Tạo fence bằng cách giữ **chuột phải và kéo một khung** trên vùng trống của desktop.
- Kéo icon vào fence (icon vào đúng ô được thả, fence sáng viền khi kéo qua); kéo ra desktop để bỏ khỏi fence. Thả lên giữa một thư mục trong fence để chuyển file vào thư mục đó.
- **Cuộn** bằng con lăn khi fence có nhiều icon hơn chỗ chứa.
- **Tab**: kéo một fence thả lên fence khác để gộp; menu chuột phải để thêm, tách hoặc xóa tab.
- **Folder Portal**: fence hiển thị nội dung một thư mục bất kỳ; nhấp đúp thư mục con để duyệt ngay trong fence, Backspace để quay lại. Thư mục hàng nghìn file vẫn mượt (chỉ dựng các icon đang nhìn thấy).
- **Quy tắc tự sắp xếp**: ví dụ ảnh vào fence "Ảnh", PDF vào "Tài liệu"; dọn desktop một lần hoặc tự động cho file mới.
- **Thu gọn** bằng nhấp đúp thanh tiêu đề; rê chuột vào để xem nhanh. **Ẩn / hiện mọi fence và icon** bằng nhấp đúp vùng trống desktop.
- Kéo mép để đổi kích thước: tự **căn vừa số cột/hàng icon**, bắt dính vào mép màn hình, fence khác và lưới.
- Nhớ **bố cục riêng cho từng cấu hình màn hình** (laptop, laptop + màn hình phụ, đổi độ phân giải).

**Làm việc với file**
- Icon trong fence là icon thật của desktop: chọn nhiều bằng kéo khung (cả giữa các fence), F2, Delete, Ctrl+C / Ctrl+V, menu chuột phải của Windows, cỡ icon của desktop.
- Tạo mới (New ▸ Folder...) hoặc dán ngay trong fence thì mục mới nằm trong fence đó; file do ứng dụng khác lưu ra desktop thì không tự lọt vào fence.
- Icon ngoài fence vẫn được căn theo lưới như "Align icons to grid".

**Giao diện**
- Nền kính mờ Acrylic (hoặc Blur / trong suốt) vẽ từ chính hình nền desktop, nằm dưới các icon; góc bo kiểu Windows 11; theo chế độ sáng/tối và màu nhấn của Windows.
- Cửa sổ Cài đặt áp dụng thay đổi ngay; hướng dẫn lần đầu có thể tạo sẵn các fence gợi ý từ desktop hiện tại.

## Phím tắt

| Phím | Tác dụng |
|---|---|
| `Ctrl` + `Alt` + `F` | Tìm trong mọi fence (Enter mở, Ctrl+Enter chỉ vị trí trong fence) |
| `Ctrl` + `Alt` + `D` | Xem desktop (như Win + D) |
| Gõ chữ trong Folder Portal | Lọc icon, không cần gõ dấu (Esc để xóa) |
| `Ctrl` + `Z` | Hoàn tác thao tác với fence (xóa, gộp, tách tab, chuyển portal, kéo icon vào / ra...) |
| `Backspace` | Quay lại thư mục trước trong Folder Portal |

## Cài đặt (người dùng)

1. Tải gói **Desko** từ trang [Releases](https://github.com/doson709/Desko/releases) (hoặc artifact `Desko` của [GitHub Actions](https://github.com/doson709/Desko/actions), hoặc tự build) rồi giải nén.
2. Chạy **`setup.bat`**:
   - tự cài **.NET 8 Desktop Runtime** nếu máy chưa có (qua winget, hoặc tải từ Microsoft);
   - cài ứng dụng vào `%LOCALAPPDATA%\Programs\Desko` (không cần quyền admin), tạo shortcut trong menu Start;
   - hỏi có khởi động cùng Windows không, rồi mở ứng dụng.
3. Gỡ cài đặt: chạy `uninstall.bat` trong thư mục cài đặt.

Windows SmartScreen có thể cảnh báo vì ứng dụng chưa được ký số: chọn **More info → Run anyway**.

## Build từ mã nguồn

Yêu cầu: Windows 10/11 x64, [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).

```powershell
.\build.ps1
```

Kết quả nằm trong `dist\`, sẵn sàng nén zip để chia sẻ:

| File | |
|---|---|
| `Desko.exe` | Ứng dụng (cần .NET 8 Desktop Runtime) |
| `DeskoGuard.exe` | Tiến trình canh gác (xem bên dưới) |
| `setup.bat`, `uninstall.bat`, `Huong dan.txt` | Bộ cài và hướng dẫn cho người dùng |

`DeskoGuard` được biên dịch bằng **Native AOT** (khoảng 1–2 MB RAM) nếu máy build có Visual Studio C++ Build Tools và **Windows SDK**; nếu không, `build.ps1` tự build nó dạng .NET thường (khoảng 6 MB RAM). GitHub Actions (`.github/workflows/build.yml`) build bản Native AOT và đính kèm gói `dist` ở mỗi lần push.

## Cách hoạt động

- **Icon thật, đặt đúng chỗ**: mỗi fence xếp các icon thành viên của nó lên lưới icon của desktop, bằng chính giao diện `IFolderView` mà Explorer dùng để đặt icon. Muốn vậy desktop phải tắt "Auto arrange icons" và "Align icons to grid"; Desko tắt chúng khi chạy và gỡ cài đặt sẽ trả lại. Icon không được hiện (fence thu gọn, tab khác, hàng đang cuộn khuất) được đặt tạm ra xa ngoài màn hình.
- **Ba lớp cho mỗi fence**: nền kính mờ là một cửa sổ con của desktop nằm *giữa hình nền và lớp icon* (Windows không làm mờ được ở đó, nên Desko tự vẽ phần hình nền bên dưới, đã làm mờ sẵn); icon của Windows nằm trên; trên cùng là một cửa sổ trong suốt chỉ nhận chuột ở thanh tiêu đề và mép, nên click vào vùng icon đi thẳng tới desktop.
- **Theo dõi desktop**: hook chuột cấp thấp biết khi bạn kéo icon (rồi đối chiếu vị trí icon để biết icon vào / ra fence nào), cuộn trong fence, hay đang kéo thả ngang qua fence (khi đó fence tạm nhận cú thả để đặt icon đúng ô và vẫn chuyển được vào thư mục trong fence). Thay đổi file trên Desktop và việc Explorer tự dựng lại danh sách icon (F5, đổi cỡ icon...) cũng được theo dõi để xếp lại ngay.
- **Luôn trả icon về**: khi thoát, mọi icon đang cất ngoài màn hình được đưa về chỗ trống gần fence của nó. Nếu bị tắt đột ngột, `DeskoGuard` (chạy kèm, chờ ứng dụng chính) gọi `Desko.exe --restore` làm việc đó; nếu cả hai cùng chết (mất điện) và ứng dụng không tự khởi động, `--restore` được đăng ký chạy lúc đăng nhập.
- **Fence là cửa sổ của lớp desktop** (owned by `Progman`), không bị Win+D làm ẩn.
- **Tự khởi động** bằng tác vụ đăng nhập của Task Scheduler (không bị Windows trì hoãn như khóa Run).
- **Nâng cấp từ bản 2.x**: bản cũ ẩn file bằng thuộc tính *Hidden* và thay Recycle Bin, This PC bằng shortcut; lần chạy đầu Desko gỡ ẩn, xóa các shortcut đó và đưa chính các icon vào fence.

## Dữ liệu và dòng lệnh

| Đường dẫn | Nội dung |
|---|---|
| `%APPDATA%\Desko\config.json` | Fence, tab, quy tắc, bố cục theo màn hình, cài đặt |
| `%APPDATA%\Desko\error.log` | Lỗi (tự xoay vòng khi quá 1 MB) |
| `%LOCALAPPDATA%\Desko\iconcache\` | Icon đã dựng sẵn cho Folder Portal và tìm kiếm |

| Lệnh | Tác dụng |
|---|---|
| `Desko.exe` | Chạy; nếu đang chạy rồi thì mở Cài đặt |
| `Desko.exe --welcome` | Mở hướng dẫn lần đầu |
| `Desko.exe --exit` | Yêu cầu bản đang chạy thoát bình thường (trả icon về) |
| `Desko.exe --autostart on\|off` | Bật / tắt khởi động cùng Windows |
| `Desko.exe --restore` | Trả mọi icon đang cất về desktop nếu ứng dụng không chạy |
| `Desko.exe --uninstall` | Thoát, trả icon về, trả lại cách sắp xếp icon cũ của desktop, bỏ khởi động cùng Windows (dùng bởi `uninstall.bat`) |

## Cấu trúc mã nguồn

```
App.xaml(.cs), Program.cs   Khởi động, các chế độ dòng lệnh, một phiên bản duy nhất
Core/        FenceManager (fence, tab, đối chiếu icon, ẩn/hiện, quy tắc, hoàn tác), DesktopHook (chuột),
             IconGrid (lưới icon), Recovery (canh gác, khôi phục), AutoStart, DisplayLayouts, Snapper, Rules,
             TestChannel (điều khiển cho kiểm thử, chỉ bật khi đặt DESKO_TEST_PIPE)
Shell/       DesktopIcons (đặt / cất icon thật của desktop), DesktopEvents, icon và cache, menu chuột phải,
             kéo thả kiểu Explorer, thao tác file, mục ảo (Recycle Bin...)
UI/          FenceWindow (+ .Icons), FenceBackdrop (nền dưới lớp icon), Wallpaper, Cài đặt, Hướng dẫn,
             Tìm kiếm, khay hệ thống, giao diện theo theme
Interop/     Khai báo Win32
Guard/       DeskoGuard: tiến trình canh gác Native AOT
installer/   setup.bat, uninstall.bat, hướng dẫn cho người dùng
```

## Hạn chế đã biết

- Desktop phải tắt "Auto arrange icons" và "Align icons to grid" (Desko tự làm, và tự căn icon ngoài fence theo lưới).
- Fence cuộn theo từng hàng icon (Windows không cho cắt một icon làm đôi ở mép fence).
- Icon đang được cất (fence thu gọn, tab khác, hàng cuộn khuất) vẫn là icon của desktop: **Ctrl+A** trên desktop cũng chọn chúng. Sau F5, chúng có thể hiện thoáng qua khoảng nửa giây trước khi được cất lại.
- Góc bo của nền fence không được khử răng cưa.
- Một số phần mềm diệt virus có thể nghi ngờ vì ứng dụng theo dõi chuột trên desktop.

## Giấy phép

[MIT](LICENSE). Desko là dự án độc lập, không liên quan đến Stardock hay sản phẩm Fences® của họ.
