FROM debian:stretch-slim

RUN apt-get update
RUN apt-get install -y --no-install-recommends --no-install-suggests \
		lib32stdc++6 \
		lib32gcc1 \
		wget \
		ca-certificates \
        python3 \
        python3-pip \
        python3-setuptools \
        htop \
        vim \
        ipython3
RUN useradd -m steam
RUN su steam -c \
		"mkdir -p /home/steam/steamcmd \
		&& cd /home/steam/steamcmd \
		&& wget -qO- 'https://steamcdn-a.akamaihd.net/client/installer/steamcmd_linux.tar.gz' | tar zxf -"
RUN apt-get clean autoclean
RUN apt-get autoremove -y
RUN rm -rf /var/lib/{apt,dpkg,cache,log}/

USER steam

WORKDIR /home/steam

RUN steamcmd/steamcmd.sh +login anonymous +force_install_dir ../games +app_update 740 +quit

RUN mkdir static

COPY . .

RUN pip3 install --user --no-cache-dir -r requirements.txt

CMD [ "python3", "main.py" ]
