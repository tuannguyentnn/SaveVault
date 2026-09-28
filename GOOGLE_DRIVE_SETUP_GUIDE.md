# Hướng Dẫn Cấu Hình Google Drive Cho SaveVault (Từ A - Z)

> 💡 **Tệp Cẩm Nang Giao Diện Thực Tế:**  
> Một bản cẩm nang đồ họa trực quan mô phỏng chi tiết 100% từng khung màn hình của Google Cloud Console đã được tạo tại:  
> 👉 [GoogleDrive_Setup_Guide.html](file:///c:/Users/TuanNguyen/Desktop/New%20folder%20(9)/SaveVault/GoogleDrive_Setup_Guide.html)  
> *(Bạn có thể nhấp đúp vào file HTML trên để mở bằng trình duyệt Chrome/Edge và xem các khung screenshot trực quan).*

---

## ⚡ Thông Số Google OAuth Bạn Vừa Tạo Thành Công!

- **Google OAuth Client ID:**  
  `xxxxxxxxxxxx-xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx.apps.googleusercontent.com`
- **Google Client Secret:**  
  `GOCSPX-xxxxxxxxxxxxxxxxxxxxxxxxxxxx`

👉 Giờ bạn chỉ cần mở **SaveVault ➔ Tab Cài đặt** và làm theo **Bước 7** bên dưới là kết nối được ngay!

---

## Các Bước Cấu Hình Chi Tiết Từ A - Z

### Bước 1: Tạo Dự Án Trên Google Cloud Console
1. Mở trình duyệt, truy cập: [https://console.cloud.google.com/](https://console.cloud.google.com/)
2. Ở thanh tiêu đề trên cùng (kế bên chữ *Google Cloud*), bấm vào nút bo viền: **Select a project** *(hoặc "Chọn một dự án")*.
3. Một popup cửa sổ mở ra ➔ Nhìn lên góc trên cùng bên phải của popup, bấm nút **NEW PROJECT** *(hoặc "Dự án mới")*.
4. **Bật xác thực 2 bước (MFA / 2FA):** Google Cloud hiện tại bắt buộc tài khoản phải bật bảo mật 2 lớp (*2-Step Verification*) trước khi tạo tài nguyên. Nếu tài khoản chưa bật, hệ thống sẽ yêu cầu bạn cài đặt qua SMS hoặc Google Authenticator. Hãy làm theo để hoàn tất.
5. Khi màn hình tạo dự án hiện ra ➔ Nhập tên: `SaveVault` vào ô **Project name** ➔ Bấm **Create (Tạo)**.

---

### Bước 2: Bật Thư Viện "Google Drive API"
1. Trên thanh tìm kiếm ở đầu trang, gõ `Google Drive API` và nhấn Enter (hoặc vào menu trái: **APIs & Services ➔ Library**).
2. Nhấp vào thẻ **Google Drive API**.
3. Nhấp nút **ENABLE (BẬT)** màu xanh dương.

---

### Bước 3: Cấu Hình "Google Auth Platform" (Giao Diện Mới)
1. Ở menu bên trái: Bấm vào **Google Auth Platform** *(hoặc APIs & Services ➔ OAuth consent screen)*.
2. Tại trang **OAuth Overview**, bạn sẽ thấy thông báo *"Google Auth Platform not configured yet"* ➔ Bấm nút màu xanh: **Get started**.
3. Thực hiện tuần tự 4 mục cấu hình (*Project configuration*):
   - **Mục 1 - App Information:**
     - **App name \*:** Điền `SaveVault`
     - **User support email \*:** Chọn email Gmail của bạn trong dropdown menu
     - Bấm **Next**.
   - **Mục 2 - Audience (CỰC KỲ QUAN TRỌNG):**
     - Tích chọn vào mục tròn: **● External** *(Available to any test user with a Google Account)*.
     - Bấm **Next**.
   - **Mục 3 - Contact Information:**
     - **Email addresses \*:** Điền lại email Gmail của bạn để Google gửi thông báo.
     - Bấm **Next**.
   - **Mục 4 - Finish & Tạo:**
     - Tích chọn vào ô vuông: **☑ I agree to the Google API Services: User Data Policy.**
     - Bấm nút **Continue**, sau đó bấm nút **Create (Tạo)** màu xanh ở góc dưới cùng bên trái.

---

### Bước 4: [BƯỚC SỐNG CÒN] Thêm Email Vào "Test Users" Tại Tab "Audience"
> ⚠️ **CẢNH BÁO QUAN TRỌNG:**  
> Vì ứng dụng cá nhân đang ở trạng thái **"Testing"**, Google chỉ cho phép tối đa 100 email trong danh sách Test Users đăng nhập. Nếu **quên bước này**, khi đăng nhập app sẽ lập tức bị chặn với lỗi:  
> `Error 403: access_denied - SaveVault has not completed the Google verification process`.

1. Nhìn sang cột menu bên trái của **Google Auth Platform** ➔ Bấm vào mục **👥 Audience**.
2. Ở trang Audience, kéo xuống phần **Test users** ➔ Bấm nút **`+ Add users`**.
3. Một bảng trượt bên phải màn hình có tiêu đề **✕ Add users** sẽ mở ra:
   - Nhập chính xác địa chỉ Gmail mà bạn sẽ dùng để đăng nhập và backup game vào ô nhập liệu.
   - Bấm nút **`Save`** màu xanh dương ở ngay bên dưới ô nhập để lưu lại.

---

### Bước 5: Tạo Mã OAuth Client ID Tại Tab "Clients"
1. Ở menu bên trái của **Google Auth Platform**: Bấm vào mục **🪪 Clients**.
2. Ở thanh trên cùng bên cạnh chữ *Clients*: Bấm nút màu xanh dương **`+ Create client`**.
3. Cấu hình thông tin Client ID:
   - **Application type \*:** Nhấp vào menu thả xuống và chọn chuẩn **Desktop app** *(Ứng dụng máy tính)*.  
     *(⚠️ Tuyệt đối không chọn Web Application vì sẽ bị lỗi cổng redirect localhost).*
   - **Name \*:** Nhập `SaveVault Desktop`.
4. Bấm nút màu xanh dương **`Create` (Tạo)** ở góc dưới bên trái.

---

### Bước 6: Lấy Client ID & Client Secret
> ⚠️ **CẢNH BÁO QUAN TRỌNG:**  
> Google **chỉ hiển thị Client Secret 1 lần duy nhất** trong popup này. Nếu đóng cửa sổ mà chưa lưu, bạn sẽ không thể xem lại Client Secret được nữa mà phải tạo lại cái mới.

1. Bấm vào biểu tượng sao chép **❐** cạnh **Client ID** để copy mã:  
   `xxxxxxxxxxxx-xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx.apps.googleusercontent.com`
2. Bấm vào biểu tượng sao chép **❐** cạnh **Client secret** để copy mã:  
   `GOCSPX-xxxxxxxxxxxxxxxxxxxxxxxxxxxx`
3. Bấm vào chữ **⬇ Download JSON** để tải file dự phòng về máy.
4. Bấm **OK** để đóng hộp thoại.

#### 💡 Mẹo: Cách Lấy Lại Client ID & Lấy Lại / Tạo Mới Secret (Nếu Quên Lưu)
Nếu bạn lỡ bấm `OK` đóng hộp thoại mà chưa kịp copy Secret:
1. Vào lại menu: **Google Auth Platform ➔ 🪪 Clients**.
2. Nhấp chuột vào tên client **`SaveVault Desktop`** để mở trang chi tiết.
3. **Lấy lại Client ID:** Khung bên phải tại mục **Additional information** ➔ Copy dòng **Client ID**.
4. **Lấy lại Client Secret (2 cách):**
   - **Cách 1 (Ưu tiên thử trước):** Bấm vào biểu tượng **Copy ❐** hoặc **Tải về JSON ⬇** ngay cạnh dòng `Client secret ****xxxx`. Nếu Google vẫn cho copy ra mã gốc hoặc mở file JSON tải về có mã bí mật ➔ Lấy dùng lại luôn, không cần tạo mới!
   - **Cách 2 (Nếu không copy được):** Bấm nút **`+ Add secret`** ở ngay bên dưới ➔ Google sẽ cấp thêm 1 Client Secret mới hiển thị đầy đủ chuỗi để bạn copy dán vào SaveVault.

---

### Bước 7: Dán Vào SaveVault & Đăng Nhập
1. Mở app **SaveVault**, chuyển sang tab **Cài đặt (Settings)** ⚙️.
2. Tại khung **Google Drive REST API**:
   - Tích chọn ô: **☑ Tùy chỉnh Google Cloud API riêng (Dành cho Developer)**.
   - Dán **Client ID** vào ô *Google OAuth Client ID*.
   - Dán **Client Secret** vào ô *Google Client Secret*.
   - Bấm nút **💾 Lưu Cấu Hình**.
3. Bấm nút **🔗 Đăng Nhập OAuth**:
   - Trình duyệt sẽ mở lên trang đăng nhập tài khoản Google của bạn.
   - Nếu thấy thông báo cảnh báo màu xám: *"Google hasn't verified this app" (Google chưa xác minh ứng dụng này)*:  
     ➔ Bấm vào chữ **Advanced (Nâng cao)** ở góc dưới bên trái  
     ➔ Bấm vào dòng **Go to SaveVault (unsafe)**  
     ➔ Bấm **Continue (Tiếp tục)** để cấp quyền.
4. Màn hình SaveVault sẽ hiển thị huy hiệu xanh: **✓ Đã kết nối (email của bạn)**.

---

## ❓ Xử Lý Lỗi Thường Gặp
- **Lỗi 403 access_denied:** Kiểm tra lại Bước 4, chắc chắn tài khoản bạn đang đăng nhập đã được thêm vào mục **Test users**.
- **Lỗi redirect_uri_mismatch:** Kiểm tra lại Bước 5, chắc chắn bạn đã chọn loại là **Desktop app** chứ không phải Web application.
