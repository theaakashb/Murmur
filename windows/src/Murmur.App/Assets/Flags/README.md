# Flag images (dictation language picker)

Country flags shown beside the languages in Settings > Transcription and the welcome guide. Segoe UI Emoji has no flag glyphs, so Windows cannot render the emoji flags the Mac shows.

- Source: **lipis/flag-icons** v7.6.0 (MIT, Copyright (c) 2013 Panayiotis Lipiridis), https://github.com/lipis/flag-icons/tree/v7.6.0
- Licence: MIT, checked at the pinned commit (the repository's only licence file; `package.json` also says MIT; no Twemoji or other third-party graphics are mixed in): https://github.com/lipis/flag-icons/blob/0d8909b00aa8418802a5abf2afa82356b129a7ef/LICENSE
- Commit: `0d8909b00aa8418802a5abf2afa82356b129a7ef` (tag `v7.6.0`)
- Files: `flags/4x3/<region>.svg`, copied unchanged. Only the 33 regions that `DictationLanguagePickerModel.FlagRegion` can return are kept (total 158,740 bytes). `LICENSE` is the upstream MIT text, SHA-256 `fc0231ccb89981e55d600f3015d8ae43adea79b2cf636da976b643d476b10336`.
- The test `EveryFlagRegionHasAnAsset` checks that each region has a file here and that the csproj bundles the folder.
- To add a flag: copy the same-commit `flags/4x3/<region>.svg`, add its hash below, and add the region to the model.

SHA-256 of each file:

```
f276f57bb3dcbcc883ab2aa5ce129deb3ff1d7e34d093606063e66b182047783  bg.svg
25d19da98eb4e4432dc3ca62bb029141d197d04d94c96a4160f3c7dfbe6af577  cn.svg
5f94f95acf2ef9f90a05594d1e96373d9dd84c834214ed79a64ac72ff5c48046  cz.svg
c17cbb0ad2efe8c9a9ddb3b7c6f0ed7f310bb2cd0aee3199ac7f056c5668d946  de.svg
edf424a2b65e7d1108b66f74d9ade6ad240dbdc7ed02b355c3cf6450c6b4a506  dk.svg
015b13f24bed077aaf2c6151833917dca334252af832863587bb435636070baa  ee.svg
d37f54eda66065acad575bebd04a85c974493e5c0bc0f05ab5b0a1c1b2cce7a7  es.svg
e0842af8b7da612c46e7e6b511168c85fba01b2b57fc65ff0d8b8cb349be3019  fi.svg
806e9ee7281d06fe0d519cff59db22419d76591c04647f81209f4032fe7b14de  fr.svg
fb7fcd235a146045b4c4dca2696898a0dd50a26251106b653566d343ddfd2c3d  gr.svg
77fd58c69d3416f1c71ddb4b627e41557305949d7d578bda4a5c21d45b70b488  hr.svg
352ebac95d22929623504ab844325d7084772eb2eb74cea7c4ab131a86a4282b  hu.svg
9e2755b5975c3dd97950ce7e49037abaff19c0cbebafd2b639f3eea0866f28e5  in.svg
f242a38036ad87c9fbc7db1b901bbc273da0c10812f783485862bce3fa547a97  it.svg
ade9d4144877f577263d1220f257fa9c8929720700568a82c0eb936bd01fb0a7  jp.svg
927d19e6e98a0f5d6342ba997d33d802ca83e898c5e7b3ec82798692fa84178d  kr.svg
9426893a004a5861061ec459108d363f0032e4861bf39873214276359d2f67aa  lt.svg
bb417347f1deeb7e67080928cd15a4c180c837ed0eaabecaa39524bda4af7cf6  lv.svg
e4d5c0a1a83384e79b90ed170d2dd74c1c841788ced2c07716d8b3da1389e3d3  mt.svg
bffc2d9c2d61931f8976fd0e3deb2f2548f1630ea3003175581109ee2d5cc4fa  nl.svg
da8375e9679486c914a662fe6e611c5821bae28d81447ce0d85732057afdc159  pk.svg
b167097a05a1b3554a0e693c69bd664d00f5bc0f6c3c1246110c3eb1de511764  pl.svg
94357d0c548a30c2234eff924b042fc6ab1118900f1ee9c658d3c458b22c254b  pt.svg
d0067aa8b99bf3878e26b9d7962ba44cbdfb420a34857786cf2681e625ef5e2c  ro.svg
a2133cff2b389e4a2fd082b13650236ab84bf12af03a3659cdaa10d97341499c  ru.svg
eabac7982c2014f944b7070ecaa76d39c93397e89a8d73577611f4b85a46a679  sa.svg
6f4b4a90ed7efd16ca5ba902f2ae8ecdfc7c528be75eafd16dbec10b0d71dd9f  se.svg
e981b97b0b659bd762274cb82b29c32bf319885a3e4c3083899ed433e3c4fe10  si.svg
e2f2d1b9b2aee186ad976fc5c0e48c045455a53ed65d710e4e6be47f6e13436b  sk.svg
c7d64cc27e99509e285d4e3fb71dbc76ee3c51cd32d72e6c2347ef1ae843e5f1  tr.svg
7cfac8d87f8c47c170ee73b097ed973648afe4a04de128e87e8662f55aea30bd  ua.svg
eb8f6fec109a1d935232edb6e74000faa6d7aaa3791def50c9b9c9eb46e6b9aa  us.svg
881cda76f853d307e6fd3f33f5a5b7e2f68f87b1c61b3c538bdbad250bccf1cb  vn.svg
```
