# Сервисная обёртка ManagerDataBase для ReportPalette.
#
# Эти файлы НЕ используются из репозитория ReportPalette напрямую.
# Их нужно скопировать в корень репозитория ManagerDataBase
# (https://github.com/volnaogt-organization/ManagerDataBase) и закоммитить:
#
#   Dockerfile                      -> <ManagerDataBase>/Dockerfile
#   requirements.txt                -> <ManagerDataBase>/requirements.txt
#   module.json                     -> <ManagerDataBase>/module.json
#   service/api.py                  -> <ManagerDataBase>/service/api.py
#   github-workflows-release.yml    -> <ManagerDataBase>/.github/workflows/release.yml
#
# После этого релиз делается тегом:
#
#   git tag v0.5.3
#   git push origin v0.5.3
#
# GitHub Actions соберёт Docker-образ в GHCR
# (ghcr.io/volnaogt-organization/managerdatabase:v0.5.3 и :latest)
# и создаст GitHub Release. ReportPalette увидит новый релиз
# и обновит контейнер модуля по кнопке в интерфейсе.
#
# Тот же набор файлов (Dockerfile + service/api.py + module.json + workflow)
# является шаблоном для будущих модулей ReportPalette.
