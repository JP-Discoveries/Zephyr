namespace Zephyr.Core.Models;

/// <summary>
/// Canonical display labels for common file extensions, used to group the dynamic
/// type-filter (so .jpg and .jpeg both show as "JPEG"). Unlisted extensions fall back
/// to the upper-cased extension.
/// </summary>
public static class FileTypeLabels
{
    public static readonly IReadOnlyDictionary<string, string> Map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        { ".jpg",  "JPEG" }, { ".jpeg", "JPEG" }, { ".jpe", "JPEG" },
        { ".tiff", "TIFF" }, { ".tif",  "TIFF" },
        { ".heic", "HEIC" }, { ".heif", "HEIF" },
        { ".mp3",  "MP3"  }, { ".m4a",  "M4A"  }, { ".m4v", "M4V"  },
        { ".mp4",  "MP4"  }, { ".m4p",  "M4P"  },
        { ".3gp",  "3GP"  }, { ".3g2",  "3G2"  },
        { ".aac",  "AAC"  }, { ".ogg",  "OGG"  }, { ".oga", "OGA"  },
        { ".flac", "FLAC" }, { ".opus", "Opus" }, { ".wma", "WMA"  },
        { ".wav",  "WAV"  }, { ".aiff", "AIFF" }, { ".aif", "AIFF" },
        { ".mkv",  "MKV"  }, { ".webm", "WebM" }, { ".avi", "AVI"  },
        { ".mov",  "MOV"  }, { ".wmv",  "WMV"  }, { ".flv", "FLV"  },
        { ".docx", "DOCX" }, { ".doc",  "DOC"  },
        { ".xlsx", "XLSX" }, { ".xls",  "XLS"  },
        { ".pptx", "PPTX" }, { ".ppt",  "PPT"  },
        { ".pdf",  "PDF"  }, { ".epub", "EPUB" },
        { ".txt",  "TXT"  }, { ".md",   "MD"   }, { ".rtf", "RTF"  },
        { ".csv",  "CSV"  }, { ".tsv",  "TSV"  },
        { ".json", "JSON" }, { ".xml",  "XML"  }, { ".yaml","YAML" }, { ".yml", "YAML" },
        { ".zip",  "ZIP"  }, { ".rar",  "RAR"  }, { ".7z",  "7Z"   },
        { ".tar",  "TAR"  }, { ".gz",   "GZ"   }, { ".bz2", "BZ2"  },
        { ".exe",  "EXE"  }, { ".dll",  "DLL"  }, { ".msi", "MSI"  },
        { ".sh",   "SH"   }, { ".bat",  "BAT"  }, { ".cmd", "CMD"  },
        { ".ps1",  "PS1"  }, { ".py",   "PY"   }, { ".js",  "JS"   },
        { ".ts",   "TS"   }, { ".cs",   "CS"   }, { ".go",  "Go"   },
        { ".rs",   "RS"   }, { ".cpp",  "CPP"  }, { ".c",   "C"    },
        { ".h",    "H"    }, { ".java", "Java" }, { ".kt",  "KT"   },
        { ".swift","Swift"}, { ".rb",   "Ruby" }, { ".php", "PHP"  },
        { ".html", "HTML" }, { ".htm",  "HTML" }, { ".css", "CSS"  },
        { ".scss", "SCSS" }, { ".vue",  "Vue"  }, { ".jsx", "JSX"  },
        { ".tsx",  "TSX"  }, { ".sql",  "SQL"  },
    };
}
