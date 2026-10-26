//! 원본 streamer(adsk.dls.streamer.constants)에서 복원한 식별자들.

/// Autodesk Fusion 애플리케이션 ID (원본 SFX RunProgram `-a` 값)
pub const FUSION_APP_ID: &str = "73e72ada57b7480280f7a6f4a289729f";
/// 기본 배포 스트림 (`-s production`)
pub const DEFAULT_STREAM: &str = "production";

/// 매니페스트 URL의 `{platform}` 자리에 들어가는 플랫폼 ID.
/// Fusion 바이너리는 Windows/macOS 빌드만 존재하므로 Linux에서는 Windows 빌드를
/// Wine 위에서 실행한다.
pub const PLATFORM_WINDOWS: &str = "67316f5e79bc48318aa5f7b6bb58243d";

/// 스트림 → CDN 루트 (constants.Channels)
pub fn channel_root(stream: &str) -> Option<&'static str> {
    match stream {
        "production" => Some("https://dl.appstreaming.autodesk.com/production/"),
        "pre-production" | "Insider" => Some("https://dl.appstreaming.autodesk.com/pre-production/"),
        "tech-preview" => Some("https://dl.appstreaming.autodesk.com/tech-preview/"),
        "release" => Some("https://dl-test.appstreaming.autodesk.com/release/"),
        "hotfix" => Some("https://dl-test.appstreaming.autodesk.com/hotfix/"),
        _ => None,
    }
}

/// constants.LocationIds – 사용자 시스템의 잘 알려진 위치
pub mod location {
    pub const REGISTRY: &str = "9568f114f430493d9f4cb4ee5d146a38";
    pub const STREAMER: &str = "195d9bce48af44bb9d185e894613e247";
    pub const SHARED: &str = "cdf165e162e1476a81cadddfc95e06bf";
    pub const COMMON_DATA: &str = "82533bdd63a14d64a6cd0b10485c044c";
    pub const WEBDEPLOY: &str = "b1f25a12d32e40548e2176b7b3c474fa";
    pub const META: &str = "2f489b6bf25c482fae7306f82e969b1c";

    pub fn is_location(id: &str) -> bool {
        [REGISTRY, STREAMER, SHARED, COMMON_DATA, WEBDEPLOY, META].contains(&id)
    }
}

/// constants.SpecialActionIds – 패키지 매니페스트의 pre/post-deploy 태스크 ID
pub mod action {
    pub const EXTERNAL_INSTALLS: &str = "3af6a9fa26704d868bb90e12b3aed9be";
    pub const CREATE_LAUNCH_ITEMS: &str = "0b9a4c1251914f7facc25616ea0f9de3";
    pub const AUTO_LAUNCH_ITEMS: &str = "14fd4e53b5d3476bb1c47d1fac493a50";
    pub const REGISTER_UNINSTALL: &str = "abd8ee230bcc49f9a56035b8eefedeed";
    pub const REGISTER_SAFE_MODE: &str = "5ca4357a038540d58fb2092e8a9329a9";
    pub const TERMINATE_PROCESSES: &str = "ec53cd6017c04fa491f5153c4bebb32e";
    pub const FILE_ASSOCIATIONS: &str = "1341bb4b8566410e9c7c8aef52bcb222";
    pub const OPEN_URLS: &str = "841696fd71df4ca3b7ec7d74f71a657d";
    pub const POST_OPEN_URLS: &str = "4b27b29bcea949b0bdd80b64ed4acecc";
    pub const ACTIVATE_CONFIG: &str = "7df3ec3a7e8d40ada66494b9dc32dc4e";
    pub const PROCESS_TEMPLATE: &str = "8df63efc00a94e9aa262d921a044c83a";
    pub const WRITE_LAUNCHER_INI: &str = "2c5d70c64a6a46008630fb0201c90351";
    pub const ADD_REGISTRY_INFO: &str = "5c461fa5ff6ec38afa078cf537137159";
    pub const FILESYSTEM_ACTIONS: &str = "1f397ebf8d2a4ca9befc3072340f035e";
    pub const WRITE_GA_APPNAME: &str = "b32513729518446bbd4984e704827353";
    pub const REGISTER_COM: &str = "adba419adf2d46a09f5ee2a05846683e";
    pub const REMOVE_WININSTALLER_PRODUCT: &str = "a9cdc10b1ed64039b7dfd3bf8a0dcd5e";
    pub const CREATE_PACKAGE_LINK: &str = "31e0f47eabdd436bb829e0c65acf67ba";
    pub const CHECK_SUPPORTED_OS: &str = "839cfbb44dc7466fa5ed8a9c7e749813";
    pub const COMPILE_PYTHON_BYTECODE: &str = "c6108a33718646818e319333ed097816";
    pub const LAUNCH_SERVICES_REGISTRATION: &str = "d740af7bfb4a46f0a028f7e3859873ff";
    pub const INITIAL_INSTALL_DELETE_FILES: &str = "1b891e3c161b456cb570ff9a05c8a9c8";
    pub const REMOVE_TEMP_MATERIAL_FOLDER: &str = "c1d5a6c7dcbf4562b683600beeae3d5f";
    pub const MOVE_MATERIAL_LIBRARY: &str = "f2a8b5c9d7e4f1a3b6c9e2f5a8b1c4d7";
    pub const WRITE_STREAMER_OPTIONS: &str = "9a6f9239365141149849b3081dc3fd7c";
    pub const REMOVE_STREAMING_SESSION: &str = "36d909d4c5304fc2a6a47bbb9116f50e";
    pub const DELIVER_ICON: &str = "4afd20f9b05b4349869fb10841d11444";
    pub const INSTALL_WEBVIEW2: &str = "7facf0de95414ab9b6315d8fa534bf91";
    pub const UPDATE_IDSDK_CONFIG: &str = "a92c95fa064f43089136f50437f73d22";
    pub const UPDATE_PINNABLE_LAUNCHER: &str = "e10f096356f7470ea58e13af02d1ba7c";
    pub const FULL_CHECKSUM_VALIDATION: &str = "76f54e3e0f374f78a31b66b8596b63b9";
    pub const CLEANUP_UPDATE_CONFIG: &str = "dab24862937e4732a279b3483a78080f";
    pub const CLEAN_CACHE: &str = "f3a0c6d1e2b6425fb1c8a7e4d9c2b3a1";

    /// 원본 ID 중 일부는 대소문자가 섞여 있어(`14fd4E53...`) 비교 전에 정규화한다.
    pub fn normalize(id: &str) -> String {
        id.to_ascii_lowercase()
    }
}

/// IDSDK 패키지의 `AdskIdentityManagerInstall.config` 파일 ID (platform_base.update_IDSDK_Config)
pub const IDSDK_CONFIG_FILE_ID: &str = "2359d677fb614e0d99482ee5929ada85";
/// IDSDK 패키지에 포함된 WebView2 설치 파일 ID
pub const WEBVIEW2_INSTALLER_FILE_ID: &str = "27a1101fcf96413c8d40d88bd262232b";

/// Autodesk 계정 로그인 콜백 URL 스킴 (브라우저 → Identity Manager)
pub const IDENTITY_URL_SCHEME: &str = "adskidmgr";
/// Fusion 문서 열기 URL 스킴 (fusion_win_configuration: file-associations.fusion360)
pub const FUSION_URL_SCHEME: &str = "fusion360";

/// 원본 precheck_config.json 의 기본 필요 공간(GB)
pub const DEFAULT_REQUIRED_GB: u64 = 8;
