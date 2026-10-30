plugins {
    id("com.android.application")
    id("org.jetbrains.kotlin.android")
}

// The version comes from the release tag (0.6.0-beta.1); versionCode counts up with it.
val rrVersion: String = (System.getenv("RR_VERSION") ?: "0.6.0-dev").removePrefix("v")
val rrCode: Int = run {
    val m = Regex("""(\d+)\.(\d+)\.(\d+)(?:-[a-z]+\.?(\d+))?""").find(rrVersion)
    if (m == null) 1 else {
        val (a, b, c) = m.destructured
        val pre = m.groupValues[4].toIntOrNull()
        // 0.6.0-beta.3 → 600003, 0.6.0 → 600099: a full release sorts after its betas.
        a.toInt() * 100_000_000 + b.toInt() * 1_000_000 + c.toInt() * 1_000 + (pre ?: 999)
    }
}

android {
    namespace = "io.github.ckenisell.resumerelay"
    compileSdk = 34

    defaultConfig {
        applicationId = "io.github.ckenisell.resumerelay"
        minSdk = 22
        targetSdk = 34
        versionCode = rrCode
        versionName = rrVersion
    }

    signingConfigs {
        create("release") {
            val ks = System.getenv("RR_KEYSTORE")
            if (ks != null && file(ks).exists()) {
                storeFile = file(ks)
                storePassword = System.getenv("RR_KEYSTORE_PASSWORD")
                keyAlias = System.getenv("RR_KEY_ALIAS") ?: "resumerelay"
                keyPassword = System.getenv("RR_KEY_PASSWORD") ?: System.getenv("RR_KEYSTORE_PASSWORD")
            }
        }
    }

    buildTypes {
        release {
            isMinifyEnabled = false
            val ks = System.getenv("RR_KEYSTORE")
            signingConfig = if (ks != null && file(ks).exists()) signingConfigs.getByName("release") else signingConfigs.getByName("debug")
        }
    }
    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }
    kotlinOptions { jvmTarget = "17" }
}

dependencies {
    implementation("androidx.media3:media3-exoplayer:1.3.1")
    implementation("androidx.media3:media3-ui:1.3.1")
}
