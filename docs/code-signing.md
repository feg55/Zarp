# Подписание Zarp для Windows

Предупреждение «Неизвестный издатель» связано с отсутствием доверенной Authenticode-подписи у `Zarp.exe`. Имя компании в свойствах EXE, SHA-256 и GitHub attestation её не заменяют.

Подпись удостоверяет издателя, но не гарантирует немедленного исчезновения SmartScreen: Microsoft отдельно оценивает репутацию приложения. Это относится и к OV, и к EV, и к Azure Artifact Signing. См. [разъяснение Microsoft](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/code-signing-options).

## GitHub Releases: SignPath

Workflow подготовлен для SignPath. Учётная запись, сертификат и секреты ещё не подключены. Сборки веток и pull request остаются неподписанными тестовыми артефактами. **Публикация по тегам `v*` останавливается, пока подписание не настроено.**

1. Создайте проект в SignPath. Для открытого проекта можно подать [заявку в SignPath Foundation](https://signpath.org/apply). Бесплатное подписание доступно после проверки [условий программы](https://signpath.org/terms); одобрение Zarp не гарантировано. В заявке укажите назначение приложения и наличие zapret2/WinDivert. При использовании сертификата Foundation издателем будет SignPath Foundation.
2. Подключите репозиторий `feg55/Zarp` к GitHub-интеграции SignPath. Настройте проект и политику релизной подписи с доверенным сертификатом и timestamp. Тестовый самоподписанный сертификат проверку релиза не пройдёт.
3. Установите [signpath-artifact.xml](signpath-artifact.xml) как конфигурацию артефакта по умолчанию. GitHub передаёт ZIP с `Zarp.exe` в корне. Подписывается внешний EXE; подписи вложенных сторонних файлов эта конфигурация не добавляет.
4. В GitHub → Settings → Secrets and variables → Actions добавьте:

   | Тип | Имя | Значение |
   | --- | --- | --- |
   | Secret | `SIGNPATH_API_TOKEN` | Токен с правом отправки запросов на подпись |
   | Variable | `SIGNPATH_ORGANIZATION_ID` | ID организации SignPath |
   | Variable | `SIGNPATH_PROJECT_SLUG` | Slug проекта |
   | Variable | `SIGNPATH_SIGNING_POLICY_SLUG` | Slug политики релизной подписи |

5. Ограничьте политику подписи своим репозиторием, релизным workflow и доверенными авторами тегов. Если сервис требует одобрения запроса, подтвердите его в SignPath; workflow ожидает завершения до 10 минут. Выполните требования Foundation к странице проекта и ролям после одобрения заявки.
6. После проверки изменений создайте новый тег версии. Workflow соберёт EXE, отправит артефакт на подпись, проверит доверие Windows и timestamp, затем рассчитает SHA-256, создаст attestation и опубликует релиз. При ошибке подписи неподписанный EXE в Releases не попадёт.

Параметры интеграции: [официальная документация SignPath](https://docs.signpath.io/trusted-build-systems/github).

## Локальная подпись своим сертификатом

Соберите `dist\Zarp.exe` обычным `build.ps1`. Установите Windows SDK с SignTool и подключите выданный удостоверяющим центром сертификат/токен в хранилище CurrentUser\My. Из Developer PowerShell выполните, заменив значения в угловых скобках:

```powershell
signtool sign /sha1 <THUMBPRINT> /s My /fd SHA256 /tr <RFC3161_TIMESTAMP_URL> /td SHA256 /d Zarp .\dist\Zarp.exe
if ($LASTEXITCODE -ne 0) { throw 'Signing failed' }
.\tools\verify-signature.ps1 -Path .\dist\Zarp.exe
Get-FileHash .\dist\Zarp.exe -Algorithm SHA256
```

Используйте timestamp-сервер своего провайдера. Для сертификата в LocalMachine добавьте `/sm`. `/sha1` выбирает сертификат по отпечатку; алгоритм подписи файла задаёт `/fd SHA256`. Ключ остаётся на токене/в хранилище, не в репозитории. См. [SignTool](https://learn.microsoft.com/en-us/windows/win32/seccrypto/signtool).

Повторная сборка перезаписывает подпись: подписывайте окончательный EXE перед распространением. Опубликованные ранее файлы автоматически подписанными не станут.

## Проверка результата

```powershell
Get-AuthenticodeSignature .\Zarp.exe | Format-List Status, SignerCertificate, TimeStamperCertificate
```

Ожидается `Status: Valid` и сертификат реального издателя. Новый подписанный файл всё ещё может вызвать SmartScreen до накопления репутации. Для ошибочного антивирусного определения отправьте именно распространяемый файл на [анализ Microsoft](https://www.microsoft.com/en-us/wdsi/filesubmission). Это отдельная процедура, не обещание снять предупреждение о репутации.

Запрос прав администратора UAC сохраняется: он нужен WinDivert и не является SmartScreen. Подпись Zarp также не меняет оценку WinDivert антивирусом.
